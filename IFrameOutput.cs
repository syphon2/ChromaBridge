using RazerSdkReader.Structures;

namespace ChromaBridge;

/// <summary>A per-key keyboard output that is fed complete Razer frames at a paced rate.</summary>
internal interface IFrameOutput
{
    bool Active { get; }
    void Start(bool isoLayout);
    void Stop();
    void Render(ChromaColor[] frame, Func<ChromaColor, (int R, int G, int B)> scale);
    string Summary();
}
