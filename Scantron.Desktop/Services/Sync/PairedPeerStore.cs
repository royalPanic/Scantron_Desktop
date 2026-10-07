using System.Text.Json;
using Scantron.Core.Models;

namespace Scantron.Desktop.Services.Sync;

/// <summary>
/// The one handheld this desktop is paired with.
/// </summary>
/// <remarks>
/// A desktop pairs with exactly one mobile unit at a time. That is a product rule, not a
/// limitation: with a single peer there is exactly one writer on the far side, so no two devices
/// can make conflicting changes to the same row simultaneously, and the merge reduces to the
/// question it is actually good at answering.
/// </remarks>
public sealed record PairedPeer(string DeviceId, string Name, string? LastAddress)
{
    /// <summary>Blank when nothing has paired yet.</summary>
    public static PairedPeer None { get; } = new("", "", null);

    public bool IsPaired => DeviceId.Length > 0;
}

/// <summary>
/// Remembers the paired handheld across restarts, and holds the pairing code in play.
/// </summary>
/// <remarks>
/// <para>
/// Stored as a small JSON file beside the workspace, written with the same temporary-then-move
/// pattern <c>WorkspaceStore</c> uses. Losing this file is not data loss - it costs one re-pair -
/// so a corrupt file is logged and treated as "not paired" rather than being allowed to stop the
/// app from starting.
/// </para>
/// <para>
/// The pairing code is deliberately <em>not</em> persisted. It only has to survive as long as it
/// takes an operator to read it off the screen and type it into the handheld, and a code that
/// outlives its pairing is a standing key to the stock count.
/// </para>
/// </remarks>
public sealed class PairedPeerStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private string _code = "";

    public PairedPeerStore(string? directory = null)
    {
        var root = directory ?? AppContext.BaseDirectory;
        _path = Path.Combine(root, "pairing.json");
        Peer = Read();
    }

    /// <summary>The paired handheld, or <see cref="PairedPeer.None"/>.</summary>
    public PairedPeer Peer { get; private set; }

    /// <summary>
    /// Current pairing code, minted on first use.
    /// </summary>
    /// <remarks>
    /// Six digits, from a cryptographic RNG. It is short enough to read off a screen and type on a
    /// keypad - which is the whole point - and short enough that it is a speed bump rather than a
    /// real secret. It is still worth having: the previous design left the endpoint open to any
    /// device on the warehouse Wi-Fi, and a code an operator must read off this PC closes that for
    /// the cost of one handshake field.
    /// </remarks>
    public string Code
    {
        get
        {
            lock (_gate)
            {
                if (_code.Length == 0)
                {
                    _code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000)
                        .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
                }

                return _code;
            }
        }
    }

    /// <summary>True when <paramref name="code"/> matches the code currently shown.</summary>
    public bool Accepts(string? code)
    {
        var shown = Code;
        return code is not null &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.ASCII.GetBytes(shown),
                   System.Text.Encoding.ASCII.GetBytes(code.Trim()));
    }

    /// <summary>Replaces the code, so a leaked one can be retired without restarting.</summary>
    public string Rotate()
    {
        lock (_gate)
        {
            _code = "";
        }

        return Code;
    }

    /// <summary>Records the paired handheld and forgets the code that admitted it.</summary>
    public void Pair(string deviceId, string name, string? address)
    {
        Peer = new PairedPeer(deviceId.Trim(), name, address);
        Save();
        Rotate();
    }

    /// <summary>Forgets the paired handheld.</summary>
    public void Unpair()
    {
        Peer = PairedPeer.None;
        Save();
        Rotate();
    }

    private PairedPeer Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return PairedPeer.None;
            }

            return JsonSerializer.Deserialize<PairedPeer>(File.ReadAllText(_path), Options) ?? PairedPeer.None;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Not fatal: the cost of forgetting is one re-pair, and refusing to start the app over
            // a lost convenience file would be far worse.
            Log.Warning($"Could not read the pairing file, so nothing is paired: {ex.Message}");
            return PairedPeer.None;
        }
    }

    private void Save()
    {
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Peer, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save the pairing file", ex);
        }
    }
}
