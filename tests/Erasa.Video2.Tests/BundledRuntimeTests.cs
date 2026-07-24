using Erasa.Video2.Core.Models;
using Erasa.Video2.Worker.Core.Services;

namespace Erasa.Video2.Tests;

public sealed class BundledRuntimeTests
{
    [Fact]
    public void Status_requires_plain_generator_export()
    {
        var root = Path.Combine(Path.GetTempPath(), "erasa-runtime-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Write(root, "python/python.exe");
            Write(root, "lama-source/saicinpainting/training/modules/ffc.py");
            Write(root, "model/config.yaml");
            Write(root, "model/generator.safetensors");
            Write(root, "model/export-metadata.json");
            File.WriteAllText(Path.Combine(root, "runtime.ready.json"), "{\"profile\":\"cuda-bundled\"}");

            var status = new RuntimeInstaller().GetStatus(root);
            Assert.Equal(RuntimeState.Ready, status.State);
            Assert.True(status.ModelReady);
            Assert.True(status.IsReady);

            File.Delete(Path.Combine(root, "model", "generator.safetensors"));
            status = new RuntimeInstaller().GetStatus(root);
            Assert.Equal(RuntimeState.Broken, status.State);
            Assert.False(status.ModelReady);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
    }
}
