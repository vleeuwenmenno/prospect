using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.Designs;

/// <summary>
/// The saved designs as the editor's dialogs use them: listing, reading and writing run on workers, one of each at a
/// time, and the frame only asks whether one has finished (<see cref="Update"/> once a frame, nothing allocated). A
/// result is kept until the dialog takes it; a result for a request the dialog has since given up (<see cref="Abandon"/>)
/// is dropped. Used from the main thread. With no folder (a scripted run without <c>--designs</c>) it does nothing and
/// <see cref="Available"/> is false.
/// </summary>
internal sealed class DesignShelf(DesignStore? store)
{
    private Task<DesignEntry[]>? _list;
    private Task<DesignSaveResult>? _save;
    private Task<DesignReadResult>? _read;
    private bool _dropList, _dropRead;

    /// <summary>True when there is a folder to save to and load from.</summary>
    public bool Available => store is not null;

    /// <summary>The store, for tests; null when <see cref="Available"/> is false.</summary>
    public DesignStore? Store => store;

    /// <summary>The newest list read; empty until the first read finishes.</summary>
    public DesignEntry[] Entries { get; private set; } = [];

    /// <summary>True from <see cref="List"/> until its result has arrived.</summary>
    public bool Listing => _list is not null;

    /// <summary>True once a list has arrived since the last <see cref="List"/> call.</summary>
    public bool Listed { get; private set; }

    /// <summary>True while a file is being read and checked for loading.</summary>
    public bool Reading => _read is not null;

    /// <summary>True while a design is being written: the dialog cannot be left then.</summary>
    public bool Saving => _save is not null;

    /// <summary>The design a read produced, or null; held until <see cref="TakeRead"/>.</summary>
    public DesignReadResult? ReadResult { get; private set; }

    /// <summary>How the last save ended, until <see cref="TakeSave"/> takes it.</summary>
    public DesignSaveResult? SaveResult { get; private set; }

    /// <summary>Starts listing the folder on a worker. A list asked for while one runs waits for that one's result, which is current enough.</summary>
    public void List()
    {
        if (store is null)
            return;
        Listed = false;
        _dropList = false;
        _list ??= Task.Run(store.List);
    }

    /// <summary>Starts reading one listed file on a worker; the result is <see cref="ReadResult"/>.</summary>
    public void Read(string file)
    {
        if (store is null || _read is not null)
            return;
        ReadResult = null;
        _dropRead = false;
        _read = Task.Run(() => store.Read(file));
    }

    /// <summary>
    /// Starts writing a design on a worker, from copies of the draft's blocks and parts made by the caller. False when
    /// there is no folder or a save is already running.
    /// </summary>
    public bool Save(string name, float blockSize, HullClass hullClass, Vector3 color, DesignBox[] boxes, DesignMount[] mounts, bool overwrite)
    {
        if (store is null || _save is not null)
            return false;
        SaveResult = null;
        _save = Task.Run(() => store.Save(name, blockSize, hullClass, color, boxes, mounts, overwrite));
        return true;
    }

    /// <summary>Takes the result of a finished save, once.</summary>
    public DesignSaveResult? TakeSave()
    {
        var result = SaveResult;
        SaveResult = null;
        return result;
    }

    /// <summary>Takes the result of a finished read, once.</summary>
    public DesignReadResult? TakeRead()
    {
        var result = ReadResult;
        ReadResult = null;
        return result;
    }

    /// <summary>Gives up on the read and the list under way: their results, when they arrive, are dropped.</summary>
    public void Abandon()
    {
        _dropList = _list is not null;
        _dropRead = _read is not null;
        Listed = false;
        ReadResult = null;
    }

    /// <summary>Collects what the workers have finished. Cheap; call once a frame.</summary>
    public void Update()
    {
        if (_list is { IsCompleted: true } list)
        {
            _list = null;
            if (!_dropList)
            {
                Entries = list.IsCompletedSuccessfully ? list.Result : [];
                Listed = true;
            }

            _dropList = false;
        }

        if (_read is { IsCompleted: true } read)
        {
            _read = null;
            if (!_dropRead)
                ReadResult = read.IsCompletedSuccessfully ? read.Result : new DesignReadResult(null, "This file could not be read.");
            _dropRead = false;
        }

        if (_save is { IsCompleted: true } save)
        {
            _save = null;
            SaveResult = save.IsCompletedSuccessfully ? save.Result : new DesignSaveResult(DesignSaveStatus.Failed, "The design could not be saved.");
        }
    }
}
