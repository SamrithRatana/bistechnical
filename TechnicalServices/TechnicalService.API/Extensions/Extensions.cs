using TechnicalService.API.Application.Queries;
using TechnicalService.API.Infrastructure;
using TechnicalService.Domain.AggregatesModel.RentalAggregate;
using TechnicalService.Domain.AggregatesModel.TechnicalAggregate;

internal static class Extensions
{
    public static void AddApplicationServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        // Add the authentication services to DI
        //builder.AddDefautlAuthentication();

        builder.Services.AddDbContext<TechnicalServiceContext>(options =>
            options.UseSqlServer(builder.Configuration.GetConnectionString("TechnicalServiceConnectionString")));

        services.AddMigration<TechnicalServiceContext, TechnicalServiceContextSeed>();

        services.AddHttpContextAccessor();

        // Configure mediatR
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining(typeof(Program));
        });

        // Register the command validators for the validator behavior (validators based on FluentValidation library)

        services.AddScoped<ITechnicalServiceQueries, TechnicalServiceQueries>();
        services.AddScoped<ITechnicalServiceRepository, TechnicalServiceRepository>();
        services.AddScoped<IRentalServiceRepository, RentalServiceRepository>();
    }
}
