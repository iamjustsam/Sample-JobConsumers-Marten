namespace JobService.Service
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Components;
    using Filters;
    using JobService.Components;
    using Marten;
    using MassTransit;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Diagnostics.HealthChecks;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Diagnostics.HealthChecks;
    using Microsoft.Extensions.Hosting;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;


    public class Startup
    {
        static bool? _isRunningInContainer;

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public static bool IsRunningInContainer =>
            _isRunningInContainer ??= bool.TryParse(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), out var inDocker) && inDocker;

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped<Token>();
            services.AddScoped<TokenActionFilter>();
            services.AddControllers(options =>
            {
                options.Filters.Add<TokenActionFilter>();
            });

            services.AddMarten(options =>
            {
                options.Connection(Configuration.GetConnectionString("JobService"));

                options.Schema.For<JobSaga>().Identity(x => x.CorrelationId);
                options.Schema.For<JobTypeSaga>().Identity(x => x.CorrelationId);
                options.Schema.For<JobAttemptSaga>().Identity(x => x.CorrelationId);
            });

            services.AddMassTransit(x =>
            {
                x.AddDelayedMessageScheduler();

                x.AddConsumer<ConvertVideoJobConsumer>(typeof(ConvertVideoJobConsumerDefinition));

                x.AddConsumer<VideoConvertedConsumer>();

                x.AddSagaRepository<JobSaga>()
                    .MartenRepository(Configuration.GetConnectionString("JobService"));
                x.AddSagaRepository<JobTypeSaga>()
                    .MartenRepository(Configuration.GetConnectionString("JobService"));
                x.AddSagaRepository<JobAttemptSaga>()
                    .MartenRepository(Configuration.GetConnectionString("JobService"));

                x.AddRequestClient<ConvertVideo>();

                x.SetKebabCaseEndpointNameFormatter();

                x.UsingRabbitMq((context, cfg) =>
                {
                    if (IsRunningInContainer)
                        cfg.Host("rabbitmq");

                    cfg.UseDelayedMessageScheduler();
                    
                    cfg.UseSendFilter(typeof(TokenSendFilter<>), context);
                    cfg.UsePublishFilter(typeof(TokenPublishFilter<>), context);
                    cfg.UseConsumeFilter(typeof(TokenConsumeFilter<>), context);

                    var options = new ServiceInstanceOptions()
                        .SetEndpointNameFormatter(context.GetService<IEndpointNameFormatter>() ?? KebabCaseEndpointNameFormatter.Instance);

                    cfg.ServiceInstance(options, instance =>
                    {
                        instance.ConfigureJobServiceEndpoints(js =>
                        {
                            js.SagaPartitionCount = 1;
                            js.FinalizeCompleted = true;

                            js.ConfigureSagaRepositories(context);
                        });

                        instance.ConfigureEndpoints(context);
                    });
                });
            });

            services.AddOpenApiDocument(cfg => cfg.PostProcess = d => d.Info.Title = "Convert Video Service");
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
                app.UseDeveloperExceptionPage();

            app.UseRouting();

            app.UseAuthorization();

            app.UseOpenApi();
            app.UseSwaggerUi3();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
                {
                    Predicate = check => check.Tags.Contains("ready"),
                    ResponseWriter = HealthCheckResponseWriter
                });

                endpoints.MapHealthChecks("/health/live", new HealthCheckOptions {ResponseWriter = HealthCheckResponseWriter});

                endpoints.MapControllers();
            });
        }

        static Task HealthCheckResponseWriter(HttpContext context, HealthReport result)
        {
            var json = new JObject(
                new JProperty("status", result.Status.ToString()),
                new JProperty("results", new JObject(result.Entries.Select(entry => new JProperty(entry.Key, new JObject(
                    new JProperty("status", entry.Value.Status.ToString()),
                    new JProperty("description", entry.Value.Description),
                    new JProperty("data", JObject.FromObject(entry.Value.Data))))))));

            context.Response.ContentType = "application/json";

            return context.Response.WriteAsync(json.ToString(Formatting.Indented));
        }
    }
}
