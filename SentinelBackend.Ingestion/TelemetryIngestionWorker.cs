namespace SentinelBackend.Ingestion;

using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using System.Text;

public class TelemetryIngestionWorker : BackgroundService
{
    private readonly EventProcessorClient _processor;
    private readonly ILogger<TelemetryIngestionWorker> _logger;

    public TelemetryIngestionWorker(
        EventProcessorClient processor,
        ILogger<TelemetryIngestionWorker> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor.ProcessEventAsync += HandleEventAsync;
        _processor.ProcessErrorAsync += HandleErrorAsync;

        await _processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        finally
        {
            await _processor.StopProcessingAsync();
        }
    }

    private async Task HandleEventAsync(ProcessEventArgs args)
    {
        var json = Encoding.UTF8.GetString(args.Data.EventBody);
        _logger.LogInformation("Received message: {Json}", json);
        await args.UpdateCheckpointAsync();
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Error processing event");
        return Task.CompletedTask;
    }
}