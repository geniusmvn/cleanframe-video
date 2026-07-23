using System.Text;
using Erasa.Video2.Core.Protocol;
using Erasa.Video2.Worker.Services;

Console.OutputEncoding = Encoding.UTF8;
Console.SetError(new StreamWriter(
    Console.OpenStandardError(),
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
{
    AutoFlush = true
});

if (args.Length != 2 || !string.Equals(args[0], "--request", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: Erasa.Video2.Worker --request <request.json>");
    return 2;
}

try
{
    var requestJson = await File.ReadAllTextAsync(args[1], Encoding.UTF8);
    var request = WorkerJson.Deserialize<WorkerRequest>(requestJson)
                  ?? throw new InvalidOperationException("Request JSON không hợp lệ.");
    var host = new WorkerHost(message =>
    {
        Console.WriteLine(WorkerJson.Serialize(message));
        Console.Out.Flush();
    });
    await host.ExecuteAsync(request, CancellationToken.None);
    return 0;
}
catch (Exception exception)
{
    var message = new WorkerMessage
    {
        Kind = "failed",
        Error = exception.Message,
        Message = exception.Message
    };
    Console.WriteLine(WorkerJson.Serialize(message));
    Console.Error.WriteLine(exception);
    return 1;
}
