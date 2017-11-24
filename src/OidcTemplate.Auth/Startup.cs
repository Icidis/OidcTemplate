﻿using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using IdentityServer4.EntityFramework.DbContexts;
using IdentityServer4.Services;
using IdentityServer4.Stores;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenSoftware.OidcTemplate.Auth.Configuration;
using OpenSoftware.OidcTemplate.Auth.Services;
using OpenSoftware.OidcTemplate.Data;
using OpenSoftware.OidcTemplate.Domain.Configuration;
using OpenSoftware.OidcTemplate.Domain.Entities;

namespace OpenSoftware.OidcTemplate.Auth
{
    public class Startup
    {
        private readonly IHostingEnvironment _env;
        public IConfigurationRoot Configuration { get; set; }
        private readonly int _sslPort = 443;
        public Startup(IHostingEnvironment env)
        {
            _env = env;

            TelemetryConfiguration.Active.DisableTelemetry = true;

            // Set up configuration sources
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json")
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables();
            if (env.IsDevelopment())
            {
                var launchConfiguration = new ConfigurationBuilder()
                    .SetBasePath(env.ContentRootPath)
                    .AddJsonFile(@"Properties\launchSettings.json")
                    .Build();
                // During development we won't be using port 443
                _sslPort = launchConfiguration.GetValue<int>("iisSettings::iisExpress:sslPort");
            }
            Configuration = builder.Build();
        }
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            var domainSettings = new DomainSettings();
            Configuration.GetSection(nameof(DomainSettings)).Bind(domainSettings);
            services.Configure<DomainSettings>(options => Configuration.GetSection(nameof(DomainSettings)).Bind(options));

            var appSettings = new AppSettings();
            Configuration.GetSection(nameof(AppSettings)).Bind(appSettings);

            var connectionString = appSettings.ConnectionStrings.AuthContext;
            var migrationsAssembly = typeof(DataModule).GetTypeInfo().Assembly.GetName().Name;

            services.AddDbContext<IdentityContext>(o => o.UseSqlServer(connectionString,
                optionsBuilder =>
                    optionsBuilder.MigrationsAssembly(typeof(DataModule).GetTypeInfo().Assembly.GetName().Name)));
            services.AddIdentity<ApplicationUser, IdentityRole>(options =>
                {
                    options.Password.RequireDigit = false;
                    options.Password.RequireLowercase = false;
                    options.Password.RequireNonAlphanumeric = false;
                    options.Password.RequireUppercase = false;
                    options.Password.RequiredLength = 6;
                })
                .AddEntityFrameworkStores<IdentityContext>()
                .AddDefaultTokenProviders();

            services.AddMvc(options =>
            {
                //options.Filters.Add(new RequireHttpsAttribute());
                options.SslPort = _sslPort;
            });

            services.AddIdentityServer(options =>
                {
                    options.UserInteraction.LoginUrl = "/Account/login";
                    options.UserInteraction.LogoutUrl = "/Account/logout";
                })
                .AddDeveloperSigningCredential()
                // Todo: fix error: Keyset does not exist exception
                //                .AddSigningCredential(MakeCert())
                .AddInMemoryApiResources(Domain.Authentication.Resources.GetApis(domainSettings.Api))
                .AddInMemoryIdentityResources(Domain.Authentication.Resources.GetIdentityResources())
                .AddOperationalStore(options =>
                {
                    options.ConfigureDbContext = builder =>
                        builder.UseSqlServer(connectionString, sql => sql.MigrationsAssembly(migrationsAssembly));

                    // this enables automatic token cleanup. this is optional.
                    options.EnableTokenCleanup = true;
                    options.TokenCleanupInterval = 30; // interval in seconds
                })
                .AddAspNetIdentity<ApplicationUser>()
                ;


            // Add application services.
            services.AddTransient<IEmailSender, EmailSender>();
            services.AddScoped<IProfileService, ProfileService>();
            services.AddScoped<IClientStore, ClientStore>();
        }

        private X509Certificate2 MakeCert()
        {
            const string thumbPrint = "92D4E2F08AF56FAE9D8D5D97C3BEE85E0FA5E038";
            X509Certificate2 cert = null;
            using (var certStore = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                certStore.Open(OpenFlags.ReadOnly);
                var certCollection = certStore.Certificates.Find(
                    X509FindType.FindByThumbprint, thumbPrint, false);
                // Get the first cert with the thumprint
                if (certCollection.Count > 0)
                {
                    // Successfully loaded cert from registry
                    cert = certCollection[0];
                }
            }
            // Fallback to local file for development
            if (cert == null)
            {
                cert = new X509Certificate2(Path.Combine(Path.Combine(_env.ContentRootPath, "Certificates"), "auth.pfx"), "export");
            }
            return cert;
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // Migrate and seed the database during startup. Must be synchronous
            try
            {
                using (var serviceScope =
                    app.ApplicationServices.GetRequiredService<IServiceScopeFactory>().CreateScope())
                {
                    serviceScope.ServiceProvider.GetService<PersistedGrantDbContext>().Database.Migrate();
                    serviceScope.ServiceProvider.GetService<IdentityContext>().Database.Migrate();
                    serviceScope.ServiceProvider.GetService<ISeedAuthService>().SeedAuthDatabase().Wait();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                //                Log.Error(ex, "Failed to migrate or seed database");
            }


            app.UseAuthentication();
            app.UseIdentityServer();

            app.UseStaticFiles();


            app.UseMvcWithDefaultRoute();

            //app.Run(async (context) =>
            //{
            //    await context.Response.WriteAsync("Hello World!");
            //});
        }
    }
}