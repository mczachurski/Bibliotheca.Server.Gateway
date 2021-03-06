﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.AspNetCore.Authorization;
using Bibliotheca.Server.Gateway.Core.Parameters;
using Bibliotheca.Server.Gateway.Core.DependencyInjections;
using Bibliotheca.Server.Mvc.Middleware.Diagnostics.Exceptions;
using Microsoft.AspNetCore.Http;
using Hangfire;
using Hangfire.MemoryStorage;
using Bibliotheca.Server.Gateway.Api.Jobs;
using Bibliotheca.Server.Gateway.Core.Policies;
using Bibliotheca.Server.Mvc.Middleware.Authorization.UserTokenAuthentication;
using Bibliotheca.Server.Gateway.Api.UserTokenAuthorization;
using Bibliotheca.Server.Mvc.Middleware.Authorization.SecureTokenAuthentication;
using Bibliotheca.Server.Mvc.Middleware.Authorization.BearerAuthentication;
using System.IO;
using Swashbuckle.AspNetCore.Swagger;
using System.Net.Http;
using Neutrino.AspNetCore.Client;
using System.Linq;
using Bibliotheca.Server.Gateway.Core.HttpClients;

namespace Bibliotheca.Server.Gateway.Api
{
    /// <summary>
    /// Startup class.
    /// </summary>
    public class Startup
    {
        private IConfigurationRoot Configuration { get; }

        private bool UseServiceDiscovery { get; set; } = true;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="env">Environment parameters.</param>
        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables();

            Configuration = builder.Build();
        }

        /// <summary>
        /// Service configuration.
        /// </summary>
        /// <param name="services">List of services.</param>
        /// <returns>Service provider.</returns>
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.Configure<ApplicationParameters>(Configuration);

            if (UseServiceDiscovery)
            {
                services.AddHangfire(x => x.UseStorage(new MemoryStorage()));
            }

            services.AddMemoryCache();
            services.AddOptions();

            services.AddCors(options =>
            {
                options.AddPolicy("AllowAllOrigins", builder =>
                {
                    builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
                });
            });

            services.AddMvc().AddJsonOptions(options =>
            {
                options.SerializerSettings.DateTimeZoneHandling = Newtonsoft.Json.DateTimeZoneHandling.Utc;
            });

            services.AddScoped<IUserTokenConfiguration, UserTokenConfiguration>();

            services.AddAuthentication(configure => {
                configure.DefaultScheme = SecureTokenSchema.Name;
            }).AddBearerAuthentication(options => {
                options.Authority = Configuration["OAuthAuthority"];
                options.Audience = Configuration["OAuthAudience"];
            }).AddSecureToken(options => {
                options.SecureToken = Configuration["SecureToken"];
            }).AddUserToken(options => { });

            services.AddApiVersioning(options =>
            {
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.ReportApiVersions = true;
                options.ApiVersionReader = ApiVersionReader.Combine( new QueryStringApiVersionReader(), new HeaderApiVersionReader( "api-version" ));
            });

            services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new Info
                {
                    Version = "v1",
                    Title = "Bibliotheca Gateway API",
                    Description = "Bibliotheca Gateway service is responsible for communication between all application microservices. Also it's main API endpoint for Bibliotheca clients such as Bibliotheca.Client (Angular SPA) or custom scripts.",
                    TermsOfService = "None"
                });

                options.AddSecurityDefinition("apiKey", new ApiKeyScheme
                {
                    Name = "Authorization",
                    Type = "apiKey",
                    In = "header",
                    Description = "As a authorization header you can send one of the following token: <br />" +
                    " - Bearer <AccessToken> - JWT token obtained by OAuth2 authorization <br />" +
                    " - SecureToken <GUID> - global token defined as a variable in services parameters <br />" +
                    " - UserToken <GUID> - token generated on user property page <br />" +
                    " - ProjectToken <GUID> - token generated on project property page"
                });

                var basePath = System.AppContext.BaseDirectory;
                var xmlPath = Path.Combine(basePath, "Bibliotheca.Server.Gateway.Api.xml"); 
                options.IncludeXmlComments(xmlPath);
            });

            services.AddNeutrinoClient(options => {
                options.SecureToken = Configuration["ServiceDiscovery:ServerSecureToken"];
                options.Addresses = Configuration.GetSection("ServiceDiscovery:ServerAddresses").GetChildren().Select(x => x.Value).ToArray();
            });

            services.AddScoped<IServiceDiscoveryRegistrationJob, ServiceDiscoveryRegistrationJob>();
            services.AddScoped<IUploaderJob, UploaderJob>();

            services.AddScoped<IHttpContextAccessor, HttpContextAccessor>();
            services.AddScoped<IHttpContextHeaders, HttpContextHeaders>();
            services.AddSingleton<HttpClient, HttpClient>();

            services.AddAuthorization(options =>
            {
                options.AddPolicy("CanAddProject", policy => policy.Requirements.Add(new HasAccessToCreateProjectRequirement()));
                options.AddPolicy("CanManageUsers", policy => policy.Requirements.Add(new HasAccessToManageUsersRequirement()));
                options.AddPolicy("CanManageGroups", policy => policy.Requirements.Add(new HasAccessToManageGroupsRequirement()));
            });

            services.AddScoped<IAuthorizationHandler, HasAccessToCreateProjectHandler>();
            services.AddScoped<IAuthorizationHandler, HasAccessToManageUsersHandler>();
            services.AddScoped<IAuthorizationHandler, ProjectAuthorizationHandler>();
            services.AddScoped<IAuthorizationHandler, UserAuthorizationHandler>();
            services.AddScoped<IAuthorizationHandler, CanUploadBranchHandler>();
            services.AddScoped<IAuthorizationHandler, HasAccessToManageGroupsHandler>();

            return services.AddApplicationModules(Configuration);
        }

        /// <summary>
        /// Configure web application.
        /// </summary>
        /// <param name="app">Application builder.</param>
        /// <param name="env">Environment parameters.</param>
        /// <param name="loggerFactory">Logger.</param>
        public void Configure(
            IApplicationBuilder app,
            IHostingEnvironment env,
            ILoggerFactory loggerFactory
        )
        {
            if(env.IsDevelopment())
            {
                loggerFactory.AddConsole(Configuration.GetSection("Logging"));
                loggerFactory.AddDebug();
            }
            else
            {
                loggerFactory.AddAzureWebAppDiagnostics();
            }

            var uploadServerOptions = new BackgroundJobServerOptions
            {
                Queues = new [] { "upload" },
                WorkerCount = 1
            };

            app.UseHangfireServer(uploadServerOptions);

            if (UseServiceDiscovery)
            {
                var defaultServerOptions = new BackgroundJobServerOptions
                {
                    Queues = new [] {"default"}
                };

                app.UseHangfireServer(defaultServerOptions);
                RecurringJob.AddOrUpdate<IServiceDiscoveryRegistrationJob>("register-service", x => x.RegisterServiceAsync(null), Cron.Minutely);
            }

            app.UseErrorHandler();

            app.UseCors("AllowAllOrigins");

            app.UseRewriteAccessTokenFronQueryToHeader();

            app.UseAuthentication();

            app.UseMvc();

            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "API v1");
            });
        }
    }
}
