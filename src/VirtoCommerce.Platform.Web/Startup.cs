using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using OpenIddict.Abstractions;
using VirtoCommerce.Assets.Abstractions;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.DynamicProperties;
using VirtoCommerce.Platform.Core.JsonConverters;
using VirtoCommerce.Platform.Core.Localizations;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Data.Extensions;
using VirtoCommerce.Platform.DistributedLock;
using VirtoCommerce.Platform.Hangfire.Extensions;
using VirtoCommerce.Platform.Modules;
using VirtoCommerce.Platform.Security.Authorization;
using VirtoCommerce.Platform.Security.Repositories;
using VirtoCommerce.Platform.Web.Azure;
using VirtoCommerce.Platform.Web.Extensions;
using VirtoCommerce.Platform.Web.Infrastructure;
using VirtoCommerce.Platform.Web.Licensing;
using VirtoCommerce.Platform.Web.Middleware;
using VirtoCommerce.Platform.Web.Migrations;
using VirtoCommerce.Platform.Web.Model.Security;
using VirtoCommerce.Platform.Web.PushNotifications;
using VirtoCommerce.Platform.Web.Redis;
using VirtoCommerce.Platform.Web.Security;
using VirtoCommerce.Platform.Web.Security.Authentication;
using VirtoCommerce.Platform.Web.Security.Authorization;
using VirtoCommerce.Platform.Web.Swagger;
using VirtoCommerce.Platform.Web.Telemetry;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace VirtoCommerce.Platform.Web
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment hostingEnvironment)
        {
            Configuration = configuration;
            WebHostEnvironment = hostingEnvironment;
        }

        public IConfiguration Configuration { get; }
        public IWebHostEnvironment WebHostEnvironment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // https://docs.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-3.1#forward-the-scheme-for-linux-and-non-iis-reverse-proxies
            if (string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED"),
                "true", StringComparison.OrdinalIgnoreCase))
            {
                services.Configure<ForwardedHeadersOptions>(options =>
                {
                    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                        ForwardedHeaders.XForwardedProto;
                    // Only loopback proxies are allowed by default.
                    // Clear that restriction because forwarders are enabled by explicit
                    // configuration.
                    options.KnownNetworks.Clear();
                    options.KnownProxies.Clear();
                });
            }

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // This custom provider allows able to use just [Authorize] instead of having to define [Authorize(AuthenticationSchemes = "Bearer")] above every API controller
            // without this Bearer authorization will not work
            services.AddSingleton<IAuthenticationSchemeProvider, CustomAuthenticationSchemeProvider>();

            services.AddRedis(Configuration);

            services.AddSignalR().AddPushNotifications(Configuration);

            services.AddOptions<PlatformOptions>().Bind(Configuration.GetSection("VirtoCommerce")).ValidateDataAnnotations();
            services.AddOptions<DistributedLockOptions>().Bind(Configuration.GetSection("DistributedLock"));
            services.AddOptions<TranslationOptions>().Configure(options =>
            {
                options.PlatformTranslationFolderPath = WebHostEnvironment.MapPath(options.PlatformTranslationFolderPath);
            });
            //Get platform version from GetExecutingAssembly
            PlatformVersion.CurrentVersion = SemanticVersion.Parse(FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);

            services.AddPlatformServices(Configuration);
            services.AddSecurityServices();

            services.AddSingleton<ICommonBlobProvider, LicenseProviderBlobStub>();
            services.AddSingleton<LicenseProvider>();

            var mvcBuilder = services.AddMvc(mvcOptions =>
            {
                //Disable 204 response for null result. https://github.com/aspnet/AspNetCore/issues/8847
                var noContentFormatter = mvcOptions.OutputFormatters.OfType<HttpNoContentOutputFormatter>().FirstOrDefault();
                if (noContentFormatter != null)
                {
                    noContentFormatter.TreatNullValueAsNoContent = false;
                }
            })
            .AddNewtonsoftJson(options =>
                {
                    //Next line needs to represent custom derived types in the resulting swagger doc definitions. Because default SwaggerProvider used global JSON serialization settings
                    //we should register this converter globally.
                    options.SerializerSettings.ContractResolver = new PolymorphJsonContractResolver();
                    //Next line allow to use polymorph types as parameters in API controller methods
                    options.SerializerSettings.Converters.Add(new StringEnumConverter());
                    options.SerializerSettings.Converters.Add(new ModuleIdentityJsonConverter());
                    options.SerializerSettings.PreserveReferencesHandling = PreserveReferencesHandling.None;
                    options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                    options.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
                    options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
                    options.SerializerSettings.Formatting = Formatting.None;
                }
            );

            services.AddSingleton(js =>
            {
                var serv = js.GetService<IOptions<MvcNewtonsoftJsonOptions>>();
                return JsonSerializer.Create(serv.Value.SerializerSettings);
            });

            services.AddDbContext<SecurityDbContext>(options =>
            {
                options.UseSqlServer(Configuration.GetConnectionString("VirtoCommerce"));
                // Register the entity sets needed by OpenIddict.
                // Note: use the generic overload if you need
                // to replace the default OpenIddict entities.
                options.UseOpenIddict();
            });

            // Enable synchronous IO if using Kestrel:
            services.Configure<KestrelServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            // Enable synchronous IO if using IIS:
            services.Configure<IISServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            var authBuilder = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                                      //Add the second ApiKey auth schema to handle api_key in query string
                                      .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.DefaultScheme, options => { })
                                      .AddCookie();

            services.AddSecurityServices(options =>
            {
            });

            services.AddIdentity<ApplicationUser, Role>(options => options.Stores.MaxLengthForKeys = 128)
                    .AddEntityFrameworkStores<SecurityDbContext>()
                    .AddDefaultTokenProviders();

            // Configure Identity to use the same JWT claims as OpenIddict instead
            // of the legacy WS-Federation claims it uses by default (ClaimTypes),
            // which saves you from doing the mapping in your authorization controller.
            services.Configure<IdentityOptions>(options =>
            {
                options.ClaimsIdentity.UserNameClaimType = OpenIddictConstants.Claims.Subject;
                options.ClaimsIdentity.UserIdClaimType = OpenIddictConstants.Claims.Name;
                options.ClaimsIdentity.RoleClaimType = OpenIddictConstants.Claims.Role;
            });

            // Support commonly used forwarded headers
            // X-Forwarded-For - Holds Client IP (optionally port number) across proxies and ends up in HttpContext.Connection.RemoteIpAddress
            // X-Forwarded-Proto - Holds original scheme (HTTP or HTTPS) even if call traversed proxies and changed and ends up in HttpContext.Request.Scheme
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.KnownProxies.Clear();
                options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor;
            });

            //Create backup of token handler before default claim maps are cleared
            var defaultTokenHandler = new JwtSecurityTokenHandler();

            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
            JwtSecurityTokenHandler.DefaultOutboundClaimTypeMap.Clear();
            authBuilder.AddJwtBearer(options =>
                    {
                        options.Authority = Configuration["Auth:Authority"];
                        options.Audience = Configuration["Auth:Audience"];

                        if (WebHostEnvironment.IsDevelopment())
                        {
                            options.RequireHttpsMetadata = false;
                        }

                        options.IncludeErrorDetails = true;

                        X509SecurityKey publicKey = null;
                        if (!Configuration["Auth:PublicCertPath"].IsNullOrEmpty())
                        {
                            var publicCert = new X509Certificate2(Configuration["Auth:PublicCertPath"]);
                            publicKey = new X509SecurityKey(publicCert);
                        }

                        options.TokenValidationParameters = new TokenValidationParameters()
                        {
                            NameClaimType = OpenIddictConstants.Claims.Subject,
                            RoleClaimType = OpenIddictConstants.Claims.Role,
                            ValidateIssuer = !string.IsNullOrEmpty(options.Authority),
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = publicKey
                        };
                    });

            var azureAdSection = Configuration.GetSection("AzureAd");

            if (azureAdSection.GetChildren().Any())
            {
                var options = new AzureAdOptions();
                azureAdSection.Bind(options);

                services.Configure<AzureAdOptions>(azureAdSection);

                if (options.Enabled)
                {
                    //TODO: Need to check how this influence to OpennIddict Reference tokens activated by this line below  AddValidation(options => options.UseReferenceTokens())
                    //TechDept: Need to upgrade to Microsoft.Identity.Web
                    //https://docs.microsoft.com/en-us/azure/active-directory/develop/microsoft-identity-web
                    authBuilder.AddOpenIdConnect(options.AuthenticationType, options.AuthenticationCaption,
                        openIdConnectOptions =>
                        {
                            switch (options.ValidateIssuer)
                            {
                                // Multitenant Azure AD issuer validation
                                // https://thomaslevesque.com/2018/12/24/multitenant-azure-ad-issuer-validation-in-asp-net-core/
                                case ValidateIssuerType.MultitenantAzureAD:
                                    openIdConnectOptions.TokenValidationParameters.IssuerValidator = MultitenantAzureADIssuerValidator.ValidateIssuerWithPlaceholder;
                                    break;
                                case ValidateIssuerType.Disabled:
                                    openIdConnectOptions.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = false };
                                    break;
                                default:
                                    // Default behaviour
                                    break;
                            }

                            openIdConnectOptions.ClientId = options.ApplicationId;

                            openIdConnectOptions.Authority = $"{options.AzureAdInstance}{options.TenantId}";
                            openIdConnectOptions.UseTokenLifetime = true;
                            openIdConnectOptions.RequireHttpsMetadata = false;
                            openIdConnectOptions.SignInScheme = IdentityConstants.ExternalScheme;
                            openIdConnectOptions.SecurityTokenValidator = defaultTokenHandler;
                            openIdConnectOptions.MetadataAddress = options.MetadataAddress;
                        });
                }
            }

            services.AddOptions<Core.Security.AuthorizationOptions>().Bind(Configuration.GetSection("Authorization")).ValidateDataAnnotations();
            var authorizationOptions = Configuration.GetSection("Authorization").Get<Core.Security.AuthorizationOptions>();
            var platformOptions = Configuration.GetSection("VirtoCommerce").Get<PlatformOptions>();
            // Register the OpenIddict services.
            // Note: use the generic overload if you need
            // to replace the default OpenIddict entities.
            services.AddOpenIddict()
                .AddCore(options =>
                {
                    options.UseEntityFrameworkCore()
                        .UseDbContext<SecurityDbContext>();
                }).AddServer(options =>
                {
                    // Register the ASP.NET Core MVC binder used by OpenIddict.
                    // Note: if you don't call this method, you won't be able to
                    // bind OpenIdConnectRequest or OpenIdConnectResponse parameters.
                    var builder = options.UseAspNetCore().
                        EnableTokenEndpointPassthrough().
                        EnableAuthorizationEndpointPassthrough();

                    // Enable the authorization, logout, token and userinfo endpoints.
                    options.SetTokenEndpointUris("/connect/token");
                    options.SetUserinfoEndpointUris("/api/security/userinfo");

                    // Note: the Mvc.Client sample only uses the code flow and the password flow, but you
                    // can enable the other flows if you need to support implicit or client credentials.
                    options.AllowPasswordFlow()
                        .AllowRefreshTokenFlow()
                        .AllowClientCredentialsFlow();

                    options.SetRefreshTokenLifetime(authorizationOptions?.RefreshTokenLifeTime);
                    options.SetAccessTokenLifetime(authorizationOptions?.AccessTokenLifeTime);

                    options.AcceptAnonymousClients();

                    // Configure Openiddict to issues new refresh token for each token refresh request.
                    // Enabled by default, to disable use options.DisableRollingRefreshTokens()

                    // Make the "client_id" parameter mandatory when sending a token request.
                    //options.RequireClientIdentification()

                    // When request caching is enabled, authorization and logout requests
                    // are stored in the distributed cache by OpenIddict and the user agent
                    // is redirected to the same page with a single parameter (request_id).
                    // This allows flowing large OpenID Connect requests even when using
                    // an external authentication provider like Google, Facebook or Twitter.
                    builder.EnableAuthorizationRequestCaching();
                    builder.EnableLogoutRequestCaching();

                    options.DisableScopeValidation();

                    // During development or when you explicitly run the platform in production mode without https, need to disable the HTTPS requirement.
                    if (WebHostEnvironment.IsDevelopment() || platformOptions.AllowInsecureHttp || !Configuration.IsHttpsServerUrlSet())
                    {
                        builder.DisableTransportSecurityRequirement();
                    }

                    // Note: to use JWT access tokens instead of the default
                    // encrypted format, the following lines are required:
                    options.DisableAccessTokenEncryption();

                    var bytes = File.ReadAllBytes(Configuration["Auth:PrivateKeyPath"]);
                    X509Certificate2 privateKey;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        // https://github.com/dotnet/corefx/blob/release/2.2/Documentation/architecture/cross-platform-cryptography.md
                        // macOS cannot load certificate private keys without a keychain object, which requires writing to disk. Keychains are created automatically for PFX loading, and are deleted when no longer in use. Since the X509KeyStorageFlags.EphemeralKeySet option means that the private key should not be written to disk, asserting that flag on macOS results in a PlatformNotSupportedException.
                        privateKey = new X509Certificate2(bytes, Configuration["Auth:PrivateKeyPassword"], X509KeyStorageFlags.MachineKeySet);
                    }
                    else
                    {
                        privateKey = new X509Certificate2(bytes, Configuration["Auth:PrivateKeyPassword"], X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
                    }
                    options.AddSigningCertificate(privateKey);
                    options.AddEncryptionCertificate(privateKey);
                });


            services.Configure<IdentityOptions>(Configuration.GetSection("IdentityOptions"));
            services.Configure<PasswordOptionsExtended>(Configuration.GetSection("IdentityOptions:Password"));
            services.Configure<PasswordLoginOptions>(Configuration.GetSection("PasswordLogin"));
            services.Configure<UserOptionsExtended>(Configuration.GetSection("IdentityOptions:User"));
            services.Configure<DataProtectionTokenProviderOptions>(Configuration.GetSection("IdentityOptions:DataProtection"));

            //always  return 401 instead of 302 for unauthorized  requests
            services.ConfigureApplicationCookie(options =>
            {
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    return Task.CompletedTask;
                };
            });

            services.AddAuthorization(options =>
            {
                //We need this policy because it is a single way to implicitly use the two schema (JwtBearer and ApiKey)  authentication for resource based authorization.
                var mutipleSchemaAuthPolicy = new AuthorizationPolicyBuilder()
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationOptions.DefaultScheme)
                    .RequireAuthenticatedUser()
                    // Customer user can get token, but can't use any API where auth is needed
                    .RequireAssertion(context =>
                        !context.User.HasClaim(OpenIddictConstants.Claims.Role, PlatformConstants.Security.SystemRoles.Customer))
                    .Build();
                //The good article is described the meaning DefaultPolicy and FallbackPolicy
                //https://scottsauber.com/2020/01/20/globally-require-authenticated-users-by-default-using-fallback-policies-in-asp-net-core/
                options.DefaultPolicy = mutipleSchemaAuthPolicy;
            });
            // register the AuthorizationPolicyProvider which dynamically registers authorization policies for each permission defined in module manifest
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            //Platform authorization handler for policies based on permissions
            services.AddSingleton<IAuthorizationHandler, DefaultPermissionAuthorizationHandler>();

            services.AddOptions<LocalStorageModuleCatalogOptions>().Bind(Configuration.GetSection("VirtoCommerce"))
                    .PostConfigure(options =>
                     {
                         options.DiscoveryPath = Path.GetFullPath(options.DiscoveryPath ?? "modules");
                     })
                    .ValidateDataAnnotations();
            services.AddModules(mvcBuilder);

            services.AddOptions<ExternalModuleCatalogOptions>().Bind(Configuration.GetSection("ExternalModules")).ValidateDataAnnotations();
            services.AddExternalModules();

            //HangFire
            services.AddHangfire(Configuration);

            // Register the Swagger generator
            services.AddSwagger(Configuration);

            // The following line enables Application Insights telemetry collection.
            // CAUTION: It is important to keep the adding AI telemetry in the end of ConfigureServices method in order to avoid of multiple
            // AI modules initialization https://virtocommerce.atlassian.net/browse/VP-6653 and  https://github.com/microsoft/ApplicationInsights-dotnet/issues/2114 until we don't
            // get rid of calling IServiceCollection.BuildServiceProvider from the platform and modules code, each BuildServiceProvider call leads to the running the
            // extra AI module and causes the hight CPU utilization and telemetry data flood on production env.
            services.AddAppInsightsTelemetry(Configuration);

            services.AddHealthChecks();

            // Add login page UI options
            var loginPageUIOptions = Configuration.GetSection("LoginPageUI");
            services.AddOptions<LoginPageUIOptions>().Bind(loginPageUIOptions);
            services.AddDatabaseDeveloperPageExceptionFilter();
            services.AddHttpClient();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseMigrationsEndPoint();
                app.UseBrowserLink();
#if DEBUG
                TelemetryDebugWriter.IsTracingDisabled = true;
#endif
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }
            
            app.UseSecurityHeaders();
            
            //Return all errors as Json response
            app.UseMiddleware<ApiErrorWrappingMiddleware>();

            // Engages the forwarded header support in the pipeline  (see description above)
            app.UseForwardedHeaders();

            app.UseHttpsRedirection();

            // Add default MimeTypes with additional bindings
            var fileExtensionsBindings = new Dictionary<string, string>()
            {
                { ".liquid", "text/html"},
                { ".md", "text/html"}
            };

            // Create default provider (with default Mime types)
            var fileExtensionContentTypeProvider = new FileExtensionContentTypeProvider();

            // Add custom bindings
            foreach (var binding in fileExtensionsBindings)
            {
                fileExtensionContentTypeProvider.Mappings[binding.Key] = binding.Value;
            }

            app.UseStaticFiles(new StaticFileOptions
            {
                ContentTypeProvider = fileExtensionContentTypeProvider
            });

            app.UseRouting();
            app.UseCookiePolicy();

            //Handle all requests like a $(Platform) and Modules/$({ module.ModuleName }) as static files in correspond folder
            app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = new PhysicalFileProvider(WebHostEnvironment.MapPath("~/js")),
                RequestPath = new PathString($"/$(Platform)/Scripts")
            });

            var localModules = app.ApplicationServices.GetRequiredService<ILocalModuleCatalog>().Modules;
            foreach (var module in localModules.OfType<ManifestModuleInfo>())
            {
                app.UseStaticFiles(new StaticFileOptions()
                {
                    FileProvider = new PhysicalFileProvider(module.FullPhysicalPath),
                    RequestPath = new PathString($"/modules/$({ module.ModuleName })")
                });
            }

            app.UseDefaultFiles();

            app.UseAuthentication();
            app.UseAuthorization();

            app.ExecuteSynchronized(() =>
            {
                // This method contents will run inside of critical section of instance distributed lock.
                // Main goal is to apply the migrations (Platform, Hangfire, modules) sequentially instance by instance.
                // This ensures only one active EF-migration ran simultaneously to avoid DB-related side-effects.

                // Apply platform migrations
                app.UsePlatformMigrations();

                app.UseDbTriggers();

                // Register platform settings
                app.UsePlatformSettings();

                // Complete hangfire init and apply Hangfire migrations
                app.UseHangfire(Configuration);

                // Register platform permissions
                app.UsePlatformPermissions();
                app.UseSecurityHandlers();
                app.UsePruneExpiredTokensJob();

                // Complete modules startup and apply their migrations
                app.UseModules();
            });

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

                //Setup SignalR hub
                endpoints.MapHub<PushNotificationHub>("/pushNotificationHub");

                endpoints.MapHealthChecks();
            });

            //Seed default users
            app.UseDefaultUsersAsync().GetAwaiter().GetResult();

            // Enable middleware to serve generated Swagger as a JSON endpoint.
            app.UseSwagger();

            // Use app insights telemetry
            app.UseAppInsightsTelemetry();

            var mvcJsonOptions = app.ApplicationServices.GetService<IOptions<MvcNewtonsoftJsonOptions>>();

            //Json converter that resolve a meta-data for all incoming objects of  DynamicObjectProperty type
            //in order to be able pass { name: "dynPropName", value: "myVal" } in the incoming requests for dynamic properties, and do not care about meta-data loading. see more details: PT-48
            mvcJsonOptions.Value.SerializerSettings.Converters.Add(new DynamicObjectPropertyJsonConverter(app.ApplicationServices.GetService<IDynamicPropertyMetaDataResolver>()));

            //The converter is responsible for the materialization of objects, taking into account the information on overriding
            mvcJsonOptions.Value.SerializerSettings.Converters.Add(new PolymorphJsonConverter());
            PolymorphJsonConverter.RegisterTypeForDiscriminator(typeof(PermissionScope), nameof(PermissionScope.Type));
        }
    }
}
