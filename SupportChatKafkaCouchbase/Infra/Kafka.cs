using Confluent.Kafka;

namespace SupportChatKafkaCouchbase.Infra;

public sealed record KafkaOptions(
    string BootstrapServers,
    string PrimaryTopic,
    string OverflowTopic,
    string ConsumerGroup);

public interface IKafkaProducer
{
    Task ProduceAsync(string topic, string key, string value, CancellationToken ct);
}

public sealed class KafkaProducer : IKafkaProducer, IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaProducer(KafkaOptions opts)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = opts.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 10
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task ProduceAsync(string topic, string key, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value }, ct);
    }

    public void Dispose() => _producer.Dispose();
}

public interface IKafkaConsumerFactory
{
    IConsumer<string, string> Create();
}

public sealed class KafkaConsumerFactory : IKafkaConsumerFactory
{
    private readonly KafkaOptions _opts;

    public KafkaConsumerFactory(KafkaOptions opts) => _opts = opts;

    public IConsumer<string, string> Create()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _opts.BootstrapServers,
            GroupId = _opts.ConsumerGroup,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnablePartitionEof = false
        };

        return new ConsumerBuilder<string, string>(config).Build();
    }
}
