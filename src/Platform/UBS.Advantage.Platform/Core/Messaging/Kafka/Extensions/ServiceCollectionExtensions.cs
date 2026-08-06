using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ubs.Advantage.Core.Infrastructure.Commands;
using Ubs.Advantage.Core.Messaging.Kafka.Models;

namespace Ubs.Advantage.Core.Messaging.Kafka.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Consumes the topic configured under <paramref name="topicKey"/> and runs
    /// <typeparamref name="TCommand"/> for each message, committing the offset only when the
    /// command reports success.
    /// </summary>
    public static IServiceCollection AddMessageConsumerService<TKey, TValue, TCommand>(
        this IServiceCollection services,
        string topicKey)
        where TCommand : ACommand<IMessage<TKey, TValue>>
    {
        services.AddKafkaOptions();
        services.TryAddSingleton<TCommand>();
        services.AddSingleton<IHostedService>(provider =>
            ActivatorUtilities.CreateInstance<MessageConsumerService<TKey, TValue, TCommand>>(provider, topicKey));

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IProducer{TKey, TValue}"/> for the topic configured under
    /// <paramref name="topicKey"/>, reporting the outcome of every send to
    /// <typeparamref name="TCommand"/>.
    /// </summary>
    public static IServiceCollection AddMessageProducerService<TKey, TValue, TCommand>(
        this IServiceCollection services,
        string topicKey)
        where TCommand : ACommand<CommandStatusParameter<IMessage<TKey, TValue>, bool>>
    {
        services.AddKafkaOptions();
        services.TryAddSingleton<TCommand>();
        services.AddSingleton<IProducer<TKey, TValue>>(provider =>
            ActivatorUtilities.CreateInstance<MessageProducerService<TKey, TValue, TCommand>>(provider, topicKey));

        return services;
    }

    /// <summary>
    /// Binds the <c>Kafka</c> configuration section once, however many services are registered.
    /// Validated at host start rather than on the first message.
    /// </summary>
    private static void AddKafkaOptions(this IServiceCollection services)
    {
        services.AddOptions<KafkaOptions>()
            .BindConfiguration(KafkaOptions.SectionName)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.BootstrapServers),
                "Kafka:BootstrapServers must be configured (non-empty).")
            .Validate(
                options => options.Topics.Count > 0,
                "Kafka:Topics must name at least one topic.")
            .ValidateOnStart();
    }
}
