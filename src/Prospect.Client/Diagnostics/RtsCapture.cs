using System.Text.Json;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Game.World;

namespace Prospect.Client.Diagnostics;

/// <summary>Muted, private capture poses and bounded numeric samples; output is written after the run.</summary>
internal sealed class RtsCapture
{
    public sealed record Pose(double X, double Y, double Z, float Distance, float Yaw, float Pitch, bool Pilot = false, bool WaitReady = true, float Hold = .5f, bool Isolate = false);
    private readonly Pose[] _poses;
    private readonly Sample[] _samples = new Sample[200000];
    private int _count;
    private long _overflow;
    private float _endSeconds;
    private int _step = -1;
    private float _started;
    private readonly string _path;
    private record struct Sample(float Seconds, int Step, float Ms, long Bytes, int Width, int Height, double X, double Y, double Z,
        double FocusX, double FocusY, double FocusZ, bool Ready, int Loaded, int Eligible, int Retained, int Hulls, int Reason, float Pixels, bool Drawn);
    public RtsCapture(string path)
    {
        _path = path;
        _poses = JsonSerializer.Deserialize<Pose[]>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (_poses.Length is < 1 or > 256 || _poses.Any(p => !double.IsFinite(p.X + p.Y + p.Z) || !float.IsFinite(p.Distance + p.Yaw + p.Pitch + p.Hold) || p.Hold < 0))
            throw new InvalidDataException("RTS script needs 1–256 finite poses and nonnegative holds.");
    }
    public int Count => _poses.Length;
    public bool Advance(WorldView world, int capture, float seconds)
    {
        if (capture >= Count || world.Galaxy is null) return false;
        if (_step != capture)
        {
            _step = capture;
            _started = seconds;
            var pose = _poses[capture];
            world.CaptureOnlySector = pose.Isolate ? world.Galaxy!.SectorOf(new Double3(pose.X, pose.Y, pose.Z)) : null;
            if (pose.Pilot) world.SetMode(ViewMode.Pilot);
            else world.SetCommandCapturePose(new Double3(pose.X, pose.Y, pose.Z), pose.Distance, pose.Yaw, pose.Pitch);
            return false;
        }
        return seconds - _started >= _poses[capture].Hold && (!_poses[capture].WaitReady || world.CaptureSectorsReady);
    }
    public void Record(WorldView world, float seconds, FrameStats frame, int width, int height, bool drawn)
    {
        _endSeconds = seconds;
        if (_step < 0 || world.Galaxy is null) return;
        if (_count == _samples.Length) { _overflow++; return; }
        var pose = _poses[0];
        var watched = new RockRef(world.Galaxy.SectorOf(new Double3(pose.X, pose.Y, pose.Z)), 0);
        var reason = world.CaptureRockReason(watched, out var pixels);
        var camera = world.CameraPosition;
        var focus = world.CommandFocus;
        _samples[_count++] = new(seconds, _step, frame.LastFrameMs, frame.ThreadAllocatedBytes, width, height,
            camera.X, camera.Y, camera.Z, focus.X, focus.Y, focus.Z, drawn && world.CaptureSectorsReady, world.GeneratedRocks,
            drawn ? world.EligibleRocks : 0, drawn ? world.VisibleRocks : 0, drawn ? world.VisibleHulls : 0, drawn ? reason : 6, pixels, drawn);
    }
    public bool Report(bool validation, int errors, float endSeconds, int savedCaptures, int failedCaptures)
    {
        _endSeconds = Math.Max(_endSeconds, endSeconds);
        var complete = _overflow == 0 && _count > 0 && _samples[_count - 1].Seconds == _endSeconds;
        var capturesComplete = savedCaptures == Count && failedCaptures == 0;
        File.WriteAllText(_path + ".csv.meta.json", JsonSerializer.Serialize(new { samples = _count, overflow = _overflow, end_seconds = _endSeconds, complete, captures_complete = capturesComplete, expected_captures = Count, saved_captures = savedCaptures, failed_captures = failedCaptures, verified = complete && capturesComplete && errors == 0 }));
        using var writer = new StreamWriter(_path + ".csv");
        writer.WriteLine("seconds,step,cpu_ms,render_bytes,width,height,camera_x,camera_y,camera_z,focus_x,focus_y,focus_z,ready,loaded,eligible,retained,hulls,watch_reason,watch_pixels,drawn");
        foreach (var s in _samples.AsSpan(0, _count))
            writer.WriteLine(FormattableString.Invariant($"{s.Seconds},{s.Step},{s.Ms},{s.Bytes},{s.Width},{s.Height},{s.X},{s.Y},{s.Z},{s.FocusX},{s.FocusY},{s.FocusZ},{s.Ready},{s.Loaded},{s.Eligible},{s.Retained},{s.Hulls},{s.Reason},{s.Pixels},{(s.Drawn ? 1 : 0)}"));
        Console.WriteLine($"RTS samples={_count} complete={complete} overflow={_overflow} end_seconds={_endSeconds} validation={validation} errors={errors} raw={_path}.csv; reason 0=not resident,1=generating,2=frustum,3=size,4=capacity,5=retained (depth occlusion not measured),6=skipped draw");
        return complete && capturesComplete && errors == 0;
    }
}
