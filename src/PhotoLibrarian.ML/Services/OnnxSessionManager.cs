using Microsoft.ML.OnnxRuntime;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Manages ONNX Runtime sessions with DirectML GPU acceleration.
/// Provides model loading, session lifecycle, and device selection.
/// </summary>
public sealed class OnnxSessionManager : IDisposable
{
    private readonly Dictionary<(string Path, bool CpuOnly), InferenceSession> _sessions = [];
    private readonly object _sync = new();
    private readonly string _modelDirectory;
    private readonly int _deviceId;

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
        return LoadModelPath(Path.Combine(_modelDirectory, modelName));
    }

    /// <summary>
    /// Loads an ONNX model from an explicit path. The normalized path is also
    /// part of the session cache key, together with CPU-only selection, so
    /// relocated models and calibrated CPU profiles cannot reuse a GPU session.
    /// </summary>
    public InferenceSession LoadModelPath(string modelPath, bool useCpuOnly = false)
    {
        lock (_sync)
        {
            modelPath = Path.GetFullPath(modelPath);
            var key = (modelPath.ToUpperInvariant(), useCpuOnly);
            if (_sessions.TryGetValue(key, out var existing))
                return existing;

            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"ONNX model not found: {modelPath}");

            InferenceSession session;
            if (useCpuOnly)
            {
                using var options = CreateSessionOptions();
                options.AppendExecutionProvider_CPU();
                session = new InferenceSession(modelPath, options);
            }
            else
            {
                try
                {
                    using var options = CreateSessionOptions();
                    options.AppendExecutionProvider_DML(_deviceId);
                    options.EnableMemoryPattern = false;
                    session = new InferenceSession(modelPath, options);
                }
                catch (OnnxRuntimeException)
                {
                    using var options = CreateSessionOptions();
                    options.AppendExecutionProvider_CPU();
                    session = new InferenceSession(modelPath, options);
                }
            }

            _sessions[key] = session;
            return session;
        }
    }

    private static SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        return options;
    }

    /// <summary>
    /// Serializes native inference across sessions sharing the DirectML device.
    /// This prevents the face and content pipelines from submitting concurrent GPU work.
    /// </summary>
    internal T RunInference<T>(Func<T> inference)
    {
        lock (_sync)
        {
            return inference();
        }
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
        UnloadModelPath(Path.Combine(_modelDirectory, modelName));
    }

    public void UnloadModelPath(string modelPath)
    {
        lock (_sync)
        {
            modelPath = Path.GetFullPath(modelPath);
            foreach (var key in _sessions.Keys.Where(key =>
                key.Path.Equals(modelPath, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (_sessions.Remove(key, out var session))
                    session.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var session in _sessions.Values)
                session.Dispose();
            _sessions.Clear();
        }
    }
}
