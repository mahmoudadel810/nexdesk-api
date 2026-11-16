using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Application.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Tenants;
using NexDesk.Application.Tickets;
using NexDesk.Application.Users;

namespace NexDesk.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SessionIssuer>();
        services.AddScoped<AuthEmails>();
        services.AddScoped<InvitationMailer>();
        services.AddScoped<TenantProvisioner>();
        services.AddScoped<TicketWriter>();

        foreach (var implementation in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var contract in implementation.GetInterfaces().Where(i => i.IsGenericType))
            {
                var definition = contract.GetGenericTypeDefinition();
                if (definition == typeof(ICommandHandler<,>))
                {
                    Decorate(services, contract, implementation, typeof(ValidatingCommandHandler<,>));
                }
                else if (definition == typeof(IQueryHandler<,>))
                {
                    Decorate(services, contract, implementation, typeof(ValidatingQueryHandler<,>));
                }
                else if (definition == typeof(IDomainEventHandler<>))
                {
                    services.AddScoped(contract, implementation);
                }
            }
        }

        return services;
    }

    private static void Decorate(IServiceCollection services, Type contract, Type implementation, Type decorator)
    {
        if (implementation.IsGenericType)
        {
            return;
        }

        var closedDecorator = decorator.MakeGenericType(contract.GetGenericArguments());
        services.AddScoped(implementation);
        services.AddScoped(contract, provider => ActivatorUtilities.CreateInstance(
            provider,
            closedDecorator,
            provider.GetRequiredService(implementation)));
    }
}
