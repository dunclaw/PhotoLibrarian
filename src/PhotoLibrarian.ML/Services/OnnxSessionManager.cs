using Microsoft.ML.OnnxRuntime;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Manages ONNX Runtime sessions with DirectML GPU acceleration.
/// Provides model loading, session lifecycle, and device selection.
/// </summary>
public sealed class OnnxSessionManager : IDisposable
{
    private readonly Dictionary<string, InferenceSession> _sessions = [];
    private readonly string _modelDirectory;
    private int _deviceId;

    public OnnxSessionManager(string? modelDirectory = null, int gpuDeviceId = 0)
    {
        _modelDirectory = modelDirectory ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoLibrarian", "Models");
        _deviceId = gpuDeviceId;
        Directory.CreateDirectory(_modelDirectory);
    }

    public string ModelDirectory => _modelDirectory;

    /// <summary>
    /// Loads an ONNX model and creates an inference session with DirectML GPU provider.
    /// Falls back to CPU if GPU is unavailable.
    /// </summary>
    public InferenceSession LoadModel(string modelName)
    {
        if (_sessions.TryGetValue(modelName, out var existing))
            return existing;

        var modelPath = Path.Combine(_modelDirectory, modelName);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model not found: {modelPath}");

        var options = new SessionOptions();

        try
        {
            // Try DirectML (GPU) first
            options.AppendExecutionProvider_DML(_deviceId);
            options.EnableMemoryPattern = false; // Required for DirectML
        }
        catch
        {
            // Fall back to CPU
            options = new SessionOptions();
            options.AppendExecutionProvider_CPU();
        }

        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;

        var session = new InferenceSession(modelPath, options);
        _sessions[modelName] = session;
        return session;
    }

    /// <summary>
    /// Checks if a model file exists in the model directory.
    /// </summary>
    public bool ModelExists(string modelName)
    {
        return File.Exists(Path.Combine(_modelDirectory, modelName));
    }

    /// <summary>
    /// Gets information about a loaded model's inputs and outputs.
    /// </summary>
    public (IReadOnlyList<NodeMetadata> Inputs, IReadOnlyList<NodeMetadata> Outputs) GetModelInfo(string modelName)
    {
        var session = LoadModel(modelName);
        var inputs = session.InputMetadata.Values.ToList();
        var outputs = session.OutputMetadata.Values.ToList();
        return (inputs, outputs);
    }

    public void UnloadModel(string modelName)
    {
        if (_sessions.Remove(modelName, out var session))
            session.Dispose();
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}
