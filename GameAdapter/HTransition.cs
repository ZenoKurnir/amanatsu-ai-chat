using BepInEx.Logging;
using Character;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amanatsu.AiChat.GameAdapter;

// Starts the game's own H scene from the chat stage and comes back when it ends.
// The main game normally reaches AL.H.HScene.InitializeAsync from EventManager.HDetail on a
// loaded map; at the title screen no map is loaded, so the resident MapManager first loads one
// with H points. Polled from Update; every step runs on the main thread.
internal sealed class HTransition
{
    // Map IDs that have H points (h/list/hpoint/object): pool, beach, water cottage,
    // shopping area, waterfall, fountain square, esthetics, rock shade, hotel.
    public static readonly int[] Maps = { 0, 1, 2, 3, 4, 5, 10, 11, 12 };

    public static string MapName(int id) => id switch
    {
        0 => L.T("プール", "Pool"), 1 => L.T("ビーチ", "Beach"), 2 => L.T("水上コテージ", "Water cottage"),
        3 => L.T("ショッピングエリア", "Shopping area"), 4 => L.T("滝", "Waterfall"), 5 => L.T("噴水広場", "Fountain square"),
        10 => L.T("エステ", "Esthetics"), 11 => L.T("岩陰", "Rock shade"), 12 => L.T("ホテル", "Hotel"),
        _ => id.ToString()
    };

    private enum Phase { Idle, MapInit, MapLoad, Humans, Running, Release, Done }

    private readonly ManualLogSource _log;
    private Phase _phase = Phase.Idle;
    private Manager.MapManager _map;
    private UniTask _pending;
    private UniTask<AL.H.ResultInfo> _scene;
    private Human _female;
    private Human _partner;
    private Human _male;
    private HumanData _femaleData;
    private HumanData _partnerData;
    private bool _girlsOnly;
    private bool _actorsLogged;
    private string _lastPosture;
    private float _nextSceneLog;
    private int _mapId;
    private int _scenesBefore;
    private AL.H.PlaceType _place;
    private AL.H.Define.PostureCategory? _category;
    private AL.H.Parameter.HState _state;
    private float _deadline;
    private Action _onFinished;
    private AL.H.HScene _hScene;
    private IntPtr _statePointer;
    private float _nextSceneFind;
    private bool _trackingStopped;
    private string _postureKey;
    private bool _tired;
    private string _spot;

    public HTransition(ManualLogSource log) { _log = log; }

    public bool Active => _phase is not (Phase.Idle or Phase.Done);

    // True while an H scene started from the chat runs; the posture patch unlocks every posture then.
    public static bool UnlockPostures { get; private set; }
    public string Status { get; private set; } = "idle";
    // What happened in the last scene; set when it starts, filled in while it runs.
    public HSceneRecord Record { get; private set; }
    public string Error { get; private set; }

    // partner: optional second female; the game then runs its 1 male / 2 females H (MFF postures).
    // girlsOnly (needs a partner): no male; the two girls play the game's girl-girl (Les) postures and the
    // player watches. The first girl takes the player-character slot, as in the main game's female-lead H.
    public void Start(HumanData female, HumanData partner, int mapId, AL.H.PlaceType place, AL.H.Define.PostureCategory? category,
        bool lewd, Action onFinished, bool girlsOnly = false)
    {
        if (girlsOnly && partner == null) throw new ArgumentException("girls-only H needs two girls");
        _girlsOnly = girlsOnly;
        if (Active) throw new InvalidOperationException("H transition already running");
        if (Array.IndexOf(Maps, mapId) < 0) throw new ArgumentException($"map {mapId} has no H points");
        _femaleData = female ?? throw new ArgumentNullException(nameof(female));
        _partnerData = partner;
        _actorsLogged = false;
        _lastPosture = null;
        _nextSceneLog = 0f;
        _mapId = mapId;
        _place = place;
        _category = category;
        _state = lewd ? AL.H.Parameter.HState.Lewdness : AL.H.Parameter.HState.Normal;
        _onFinished = onFinished;
        Record = new HSceneRecord { GirlsOnly = girlsOnly, Place = MapName(mapId) };
        _postureKey = null;
        _tired = false;
        _spot = null;
        _hScene = null;
        _statePointer = IntPtr.Zero;
        _nextSceneFind = 0f;
        _trackingStopped = false;
        Error = null;
        _map = null;
        Enter(Phase.MapInit, 30f);
    }

    public void Update()
    {
        if (!Active) return;
        try
        {
            switch (_phase)
            {
                case Phase.MapInit:
                    // The MapManager lives from startup; ChangeMapAsync silently does nothing until its map list is read.
                    _map = Manager.MapManager.Initialized ? Manager.MapManager.Instance : null;
                    var table = _map?._mapListTable;
                    if (table == null || !table.ContainsKey(_mapId)) { Timeout(); return; }
                    _scenesBefore = UnityEngine.SceneManagement.SceneManager.sceneCount;
                    _pending = _map.ChangeMapAsync(_mapId, 0, true, null, true, false);
                    Enter(Phase.MapLoad, 60f);
                    break;
                case Phase.MapLoad:
                    if (!Completed(_pending)) { Timeout(); return; }
                    if (_map._mapID != _mapId) throw new InvalidOperationException($"map {_mapId} did not load (current {_map._mapID})");
                    _log.LogInfo($"H map loaded: id={_mapId}, scenes {_scenesBefore} -> {UnityEngine.SceneManagement.SceneManager.sceneCount}");
                    Enter(Phase.Humans, 10f);
                    break;
                case Phase.Humans:
                    var humans = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Human>(3);
                    _female = Human.Create(_femaleData);
                    if (_girlsOnly)
                    {
                        _partner = Human.Create(_partnerData);
                        humans[0] = _female;
                        humans[1] = _partner;
                        _category = AL.H.Define.PostureCategory.Les;
                    }
                    else
                    {
                        _male = Human.Create(MaleData());
                        humans[0] = _male;
                        humans[1] = _female;
                        // The third slot is the second female of the game's paired (3P) H.
                        if (_partnerData != null)
                        {
                            _partner = Human.Create(_partnerData);
                            humans[2] = _partner;
                        }
                    }
                    var category = _category.HasValue
                        ? new Il2CppSystem.Nullable<AL.H.Define.PostureCategory>(_category.Value)
                        : new Il2CppSystem.Nullable<AL.H.Define.PostureCategory>();
                    var parameter = new AL.H.Parameter(_state, AL.H.Parameter.HPattern.Normal, category, _place, null, humans);
                    UnlockPostures = true;
                    _scene = AL.H.HScene.InitializeAsync(parameter, new Il2CppSystem.Threading.CancellationToken());
                    _log.LogInfo($"H scene requested: place={_place}, category={_category?.ToString() ?? "any"}, state={_state}, partner={_partner != null}, girlsOnly={_girlsOnly}");
                    Enter(Phase.Running, float.PositiveInfinity);
                    break;
                case Phase.Running:
                    LogScene();
                    if (!_scene.GetAwaiter().IsCompleted) { Track(); return; }
                    _log.LogInfo("H scene ended.");
                    ReadResult();
                    Release();
                    break;
                case Phase.Release:
                    // The release task unloads the map scene and then keeps waiting on asset cleanup
                    // without reporting completion at the title; the map scene being gone is enough.
                    if (!Completed(_pending) && UnityEngine.SceneManagement.SceneManager.sceneCount > _scenesBefore)
                    {
                        if (Time.realtimeSinceStartup <= _deadline) return;
                        _log.LogWarning("H map release did not finish; returning to the chat anyway.");
                    }
                    Finish();
                    break;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _log.LogError($"H transition failed in {_phase}: {ex}");
            try { Release(); Finish(); } catch (Exception cleanup) { _log.LogError($"H cleanup failed: {cleanup}"); _phase = Phase.Done; }
        }
    }

    private static bool Completed(UniTask task)
    {
        var status = task.Status;
        if (status == UniTaskStatus.Pending) return false;
        if (status != UniTaskStatus.Succeeded) throw new InvalidOperationException($"game task ended as {status}");
        return true;
    }

    private void Enter(Phase phase, float seconds)
    {
        _phase = phase;
        Status = phase.ToString();
        _deadline = Time.realtimeSinceStartup + seconds;
    }

    private void Timeout()
    {
        if (Time.realtimeSinceStartup > _deadline) throw new TimeoutException($"{_phase} did not finish");
    }

    // The player's own card if there is one, otherwise the game's default male.
    // Male card path from the H settings; empty for the game's default male.
    public string MaleCard { get; set; }

    private HumanData MaleData()
    {
        var data = new HumanData((byte)0);
        var card = string.IsNullOrWhiteSpace(MaleCard) || !File.Exists(MaleCard) ? null : MaleCard;
        _log.LogInfo($"H male: {card ?? "default"}");
        if (card != null && data.LoadCharaFile(card)) return data;
        // The game's built-in male preset (an empty HumanData has no proper male look).
        data = new HumanData((byte)0);
        if (!data.LoadFromPreset(0)) _log.LogWarning("Built-in male preset could not be loaded.");
        return data;
    }

    // With a second female, logs the actors the scene built and each posture's character mix (MF/MFF/FF),
    // so a test run shows whether the game treats the scene as 3P.
    private void LogScene()
    {
        if (_partner == null || Time.realtimeSinceStartup < _nextSceneLog) return;
        _nextSceneLog = Time.realtimeSinceStartup + .5f;
        try
        {
            var scene = UnityEngine.Object.FindObjectOfType<AL.H.HScene>();
            if (scene == null || !scene.Loaded) return;
            if (!_actorsLogged)
            {
                _actorsLogged = true;
                var actors = scene.Actors;
                for (var i = 0; actors != null && i < actors.Length; i++)
                    _log.LogInfo(actors[i] == null ? $"H actor {i}: none"
                        : $"H actor {i}: name={actors[i].Name}, sex={actors[i].Sex}, type={actors[i].ActorType}, active={actors[i].Active}");
            }
            var info = scene.NowAnimationInfo;
            if (info == null || info.NameAnimation == _lastPosture) return;
            _lastPosture = info.NameAnimation;
            _log.LogInfo($"H posture: id={info.ID}, mode={info.Mode}, name={info.NameAnimation}, mix={info.PromiscuityType}");
        }
        catch (Exception ex)
        {
            _nextSceneLog = float.PositiveInfinity;
            _log.LogWarning($"H scene logging stopped: {ex.Message}");
        }
    }

    // Follows the motion state of the running scene: each climax, where the player came, swallowing or spitting,
    // and the girl's exhausted state. Polled every frame; a state object only changes on a real transition.
    private void Track()
    {
        if (_trackingStopped) return;
        try
        {
            if (_hScene == null)
            {
                if (Time.realtimeSinceStartup < _nextSceneFind) return;
                _nextSceneFind = Time.realtimeSinceStartup + .5f;
                _hScene = UnityEngine.Object.FindObjectOfType<AL.H.HScene>();
                if (_hScene == null) return;
            }
            if (!_hScene.Loaded) return;
            var info = _hScene.NowAnimationInfo;
            if (info == null) return;
            var category = ((AL.H.Define.PostureCategory)info.Mode).ToString();
            var spot = _hScene.PlaceCtrl?.PlaceType.ToString();
            if (spot != _spot) { _spot = spot; Record.OnSpot(spot); _log.LogDebug($"H spot: {spot}"); }
            var key = $"{info.Mode}/{info.ID}";
            if (key != _postureKey)
            {
                // The girls this posture uses: both for MFF/FF, otherwise the one girl the game has active.
                var both = info.PromiscuityType != AL.H.PromiscuityType.MF;
                var girls = new List<string>();
                var actors = _hScene.Actors;
                for (var i = 0; actors != null && i < actors.Length; i++)
                    if (actors[i] != null && !actors[i].IsManOrFutanari && (both || actors[i].Active)) girls.Add(actors[i].Name);
                if (!both && girls.Count > 1) girls.RemoveRange(1, girls.Count - 1);
                _postureKey = key;
                var anal = info.NameAnimation?.Contains("アナル") == true;
                Record.OnPosture(category, girls.ToArray(), anal, info.NameAnimation);
                _log.LogDebug($"H posture girls: {string.Join(", ", girls)} ({category}{(anal ? ", anal" : "")})");
            }
            var state = _hScene.Controller?.State;
            if (state != null && state.Pointer != _statePointer)
            {
                _statePointer = state.Pointer;
                var name = state.GetIl2CppType().Name;
                _log.LogDebug($"H state: {name} ({category})");
                Record.OnState(name);
            }
            if (_hScene.IsWeakness && !_tired) { _tired = true; Record.OnTired(); }
        }
        catch (Exception ex)
        {
            _trackingStopped = true;
            _log.LogWarning($"H scene tracking stopped: {ex.Message}");
        }
    }

    // The game's own result: the exhausted state, plus counts logged for comparison with the tracked states.
    private void ReadResult()
    {
        try
        {
            var result = _scene.GetAwaiter().GetResult();
            if (result == null) return;
            if (result.IsWeakness && !_tired) { _tired = true; Record.OnTired(); }
            var counter = result.Counter;
            _log.LogInfo($"H result: weakness={result.IsWeakness}, outside={counter?.Outside}, lewdness={counter?.IsLewdness}");
        }
        catch (Exception ex) { _log.LogWarning($"H result could not be read: {ex.Message}"); }
    }

    private void Release()
    {
        _hScene = null;
        UnlockPostures = false;
        foreach (var human in new[] { _female, _partner, _male })
            if (human != null) try { human.Dispose(); } catch { }
        _female = _partner = _male = null;
        _partnerData = null;
        _pending = _map != null ? _map.ReleaseMapObject() : UniTask.CompletedTask;
        Enter(Phase.Release, 30f);
    }

    private void Finish()
    {
        // H ends by covering the screen (Fade.In); the main game uncovers it after restoring the map.
        try { Manager.Scene.StartFade(FadeCanvas.Fade.Out, false); }
        catch (Exception ex) { _log.LogWarning($"Scene fade could not be cleared: {ex.Message}"); }
        var scenes = new List<string>();
        for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            scenes.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name);
        _log.LogInfo($"H finished; loaded scenes: {string.Join(", ", scenes)}");
        _map = null;
        _phase = Phase.Done;
        Status = Error == null ? "done" : "failed";
        _onFinished?.Invoke();
    }
}
