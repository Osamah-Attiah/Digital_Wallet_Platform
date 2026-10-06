using Testcontainers.RabbitMq;
using RabbitMQ.Client;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class RabbitDatabaseFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4.1.4-management-alpine").Build();
    public string ConnectionString => _container.GetConnectionString();
    public Task InitializeAsync() => StartAsync();
    public Task StopAsync() => _container.StopAsync();
    public async Task StartAsync()
    {
        await _container.StartAsync();
        // Reused container logs can satisfy an old startup marker before AMQP is ready again.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await using var connection = await new ConnectionFactory { Uri = new Uri(ConnectionString),
                    AutomaticRecoveryEnabled = false, RequestedConnectionTimeout = TimeSpan.FromSeconds(1),
                    HandshakeContinuationTimeout = TimeSpan.FromSeconds(1) }.CreateConnectionAsync(attempt.Token);
                return;
            }
            catch (Exception) when (!deadline.IsCancellationRequested) { await Task.Delay(100, deadline.Token); }
        }
    }
    public async Task DisposeAsync() => await _container.DisposeAsync();
}
