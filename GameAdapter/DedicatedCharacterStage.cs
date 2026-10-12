using BepInEx.Logging;
using Amanatsu.AiChat.Config;
using Character;
using ILLGAMES.ADV;
using ILLGAMES.Unity.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Amanatsu.AiChat.GameAdapter;

// Owns a standalone instance of the game's ADV presentation.
internal sealed class DedicatedCharacterStage : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly ModSettings _settings;
    private ADVCore _core;
    // Slot 0 is the main character; slot 1 is the optional second character standing beside her.
    private sealed class Actor
    {
        public Human Human;
        public Animator Animator;
        public int IdlePose = -1;
        public int RequestedMotion;
        public string CardPath;
    }
    private readonly Actor[] _actors = { new(), new() };
    private Human _human { get => _actors[0].Human; set => _actors[0].Human = value; }
    private bool _requested;
    private bool _loaded;
    private bool _ready;
    private bool _failed;
    private float _nextAttempt;
    private string _cardPath;
    private string _activeCardPath;
    private readonly List<(CanvasGroup Group, bool Added, float Alpha, bool Interactable, bool Raycasts)> _titleGroups = new();
    private readonly HeroineDialogueFrame _dialogueFrame = new();
    private readonly BackgroundBackdrop _background = new();
    private readonly ConversationPostProcessing _postProcessing = new();
    private bool _cameraGraphicsCaptured;
    private bool _oldPostProcessing;
    private LayerMask _oldVolumeMask;
    private Transform _oldVolumeTrigger;
    private AntialiasingMode _oldAntialiasing;
    private Camera _camera;
    private GameObject _light;
    private Light _oldSun;
    private Animator _animator { get => _actors[0].Animator; set => _actors[0].Animator = value; }
    private string[] _cards = Array.Empty<string>();
    private int _cardIndex;
    private float _distance = 0.75f;
    private const float MinimumDistance = 0.05f;
    private const float MaximumDistance = 2.5f;
    private float _height = 16f;
    private float _yaw;
    private float _pitch = 3f;
    private Vector3 _pan;
    private bool _viewUnlocked;
    private CursorLockMode _oldCursorLock;
    private bool _oldCursorVisible;
    private readonly GameObject _uiRoot;
    private string _lastText = "";
    private int _requestedMotion { get => _actors[0].RequestedMotion; set => _actors[0].RequestedMotion = value; }
    private bool _eyesFollowCamera = true;
    private bool _neckFollowsCamera;
    private bool _builtInPreset;
    private float _targetHeight = .72f;
    private bool _cameraInitialized;
    private int _dragButton = -1;
    private Canvas _nativeCanvas;
    private CanvasGroup _nativeCanvasGroup;
    public bool NativeWindowVisible => _nativeCanvas != null && _nativeCanvas.enabled &&
        _nativeCanvas.gameObject.activeInHierarchy && _nativeCanvasGroup != null && _nativeCanvasGroup.alpha > .01f;

    public DedicatedCharacterStage(GameObject uiRoot, ManualLogSource log, ModSettings settings)
    { _log = log; _uiRoot = uiRoot; _settings = settings; }
    public Human Human => _human;
    public string CharacterName => _human?.Data?.Parameter?.fullname;
    public Human PartnerHuman => _actors[1].Human;
    public bool HasPartner => _actors[1].Human != null;
    public string PartnerName => _actors[1].Human?.Data?.Parameter?.fullname;
    public string PartnerCardPath => _actors[1].CardPath;
    public Human HumanAt(int slot) => slot is 0 or 1 ? _actors[slot].Human : null;
    // Where a character stands now (they can swap places); 0 when she is not on stage.
    public int SlotOf(Human human) => human != null && _actors[1].Human != null && _actors[1].Human.Pointer == human.Pointer ? 1 : 0;
    public string NameAt(int slot) => HumanAt(slot)?.Data?.Parameter?.fullname;
    public string ActiveCardPath => _activeCardPath;
    public bool IsReady => _ready && !_failed;
    public bool EyesFollowCamera => _eyesFollowCamera;
    public bool NeckFollowsCamera => _neckFollowsCamera;
    public string Status => _failed ? "ADV initialization failed" : IsReady ? "ADV ready" : "loading ADV";

    public bool Enter(string cardPath, bool builtInPreset = false)
    {
        _cardPath = cardPath;
        _builtInPreset = builtInPreset;
        _requested = true;
        _failed = false;
        return IsReady;
    }

    public void Maintain()
    {
        if (!_requested || _failed || _ready || Time.realtimeSinceStartup < _nextAttempt) return;
        _nextAttempt = Time.realtimeSinceStartup + 1f;
        try
        {
            if (!_loaded)
            {
                if (SceneManager.GetActiveScene().name != "Title") return;
                ADVSetup.Load();
                _loaded = true;
                _log.LogInfo("Standalone ADV load requested.");
                return;
            }
            _core = ADVSetup.Core;
            if (_core?.Scenario?.TextController == null) return;
            var scenario = _core.Scenario;
            _nativeCanvas = scenario._windowImage?.GetComponentInParent<Canvas>();
            if (_nativeCanvas != null)
            {
                _nativeCanvasGroup = _nativeCanvas.GetComponent<CanvasGroup>();
                if (_nativeCanvasGroup == null) _nativeCanvasGroup = _nativeCanvas.gameObject.AddComponent<CanvasGroup>();
                _nativeCanvasGroup.alpha = 1f;
                _nativeCanvasGroup.blocksRaycasts = true;
                _nativeCanvasGroup.interactable = true;
            }
            _log.LogInfo($"ADV bound: object={_core.gameObject.name}, scene={_core.gameObject.scene.name}, textInitialized={scenario.TextController.Initialized}");
            scenario.enabled = false;
            var data = new HumanData((byte)1);
            var path = string.IsNullOrWhiteSpace(_cardPath) ? null : Path.GetFullPath(_cardPath);
            if (_builtInPreset)
            { if (!data.LoadFromPreset(1)) throw new InvalidDataException("Built-in character preset could not be loaded."); }
            else if (path == null || !data.LoadCharaFile(path)) throw new InvalidDataException("Female character card could not be loaded.");
            _activeCardPath = path;
            _log.LogInfo($"ADV creating character: {path}");
            _human = Character.Human.Create(data);
            _log.LogInfo($"ADV character created: {_human?.GameObject?.name}");
            _cards = Directory.GetFiles(Path.GetFullPath("UserData/chara/female"), "*.png").OrderBy(p => p).ToArray();
            _cardIndex = Array.FindIndex(_cards, p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            SetupCharacter();
            _core.gameObject.SetActive(true);
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            {
                if (canvas.gameObject.scene.name != "Title" || canvas.transform.IsChildOf(_uiRoot.transform)) continue;
                var group = canvas.GetComponent<CanvasGroup>();
                var added = group == null;
                if (added) group = canvas.gameObject.AddComponent<CanvasGroup>();
                if (group == null) continue;
                _titleGroups.Add((group, added, group.alpha, group.interactable, group.blocksRaycasts));
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;
            }
            _camera = _core.CharaCamera ?? _core.EnvCamera;
            _log.LogInfo($"ADV camera={_camera?.name}, env={_core.EnvCamera?.name}, chara={_core.CharaCamera?.name}");
            if (_camera != null)
            {
                _camera.gameObject.SetActive(true);
                _camera.enabled = true;
                _camera.cullingMask = ~(1 << 5);
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.14f, 0.22f, 0.27f);
                _camera.fieldOfView = Mathf.Clamp(_settings.FieldOfView.Value, 20f, 70f);
                _camera.nearClipPlane = 0.03f;
                _camera.farClipPlane = Mathf.Max(_camera.farClipPlane, 200f);
                SetCameraPreset(_settings.CameraPreset.Value);
                UpdateCamera(true);
                ApplyGazeMode();
                try { _background.Apply(_camera, _settings.BackgroundImage.Value); }
                catch (Exception ex) { _log.LogWarning($"Background image unavailable: {ex.Message}"); }
            }
            // Mirrors the map's CameraLight prefab: a camera-relative key light that
            // only affects character layers (7, 9), with the game's soft shadows.
            _light = new GameObject("AiConversationLight");
            var light = _light.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = (1 << 7) | (1 << 9);
            light.shadows = AiChatBehaviour.Graphics.ShadowType.Value;
            light.shadowStrength = 1f;
            if (_camera != null) _light.transform.SetParent(_camera.transform, false);
            ApplyLightSettings();
            _oldSun = RenderSettings.sun;
            RenderSettings.sun = light;
            scenario.VisibleWindowForce = true;
            scenario.TextController.Set(CharacterName ?? L.T("AI会話", "AI Chat"), "……");
            scenario.TextController.ForceCompleteDisplayText();
            _dialogueFrame.Apply(scenario._windowImage, _log);
            _ready = true;
            AiChatBehaviour.Graphics.Enter();
            RefreshGraphics();
            _log.LogInfo("Native ADV message window ready.");
        }
        catch (Exception ex)
        {
            _failed = true;
            _log.LogError($"Standalone ADV initialization failed: {ex}");
            Exit(); _failed = true;
        }
    }

    // speaker: the slot whose name the window shows; moreTurns: another character speaks after this,
    // so the last page keeps ▼ and the reader's next page turn hands over to her (WaitingForNextTurn).
    public bool ShowText(string text, int speaker = 0, bool moreTurns = false)
    {
        if (!IsReady) return false;
        _pages.Clear();
        _pages.AddRange(Sequence.DialoguePages.Split(text ?? ""));
        _page = 0;
        _lastText = text;
        _speaker = HumanAt(speaker) != null ? speaker : 0;
        _moreTurns = moreTurns;
        DisplayPage();
        _log.LogInfo($"Native ADV text displayed: speaker={NameAt(_speaker)}, characters={text?.Length ?? 0}, pages={_pages.Count}, moreTurns={moreTurns}");
        return true;
    }

    // Long replies are split into pages; the window shows one page and ▼ while more follow.
    private readonly List<string> _pages = new();
    private int _page;
    private int _speaker;
    private bool _moreTurns;
    public bool HasMorePages => _page + 1 < _pages.Count || _moreTurns;
    public bool WaitingForNextTurn => _moreTurns;

    public bool NextPage()
    {
        if (!IsReady || !HasMorePages) return false;
        if (_page + 1 < _pages.Count) { _page++; DisplayPage(); }
        else _moreTurns = false;
        return true;
    }

    private void DisplayPage()
    {
        _core.Scenario.VisibleWindowForce = true;
        var text = _pages[_page] + (HasMorePages ? " ▼" : "");
        _core.Scenario.TextController.Set(NameAt(_speaker) ?? CharacterName ?? L.T("AI会話", "AI Chat"), text);
    }

    // True when the screen point is on the native dialogue window.
    public bool DialogueWindowContains(Vector2 screenPoint)
    {
        var image = IsReady ? _core.Scenario._windowImage : null;
        if (image == null) return false;
        var canvas = image.canvas;
        var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        return RectTransformUtility.RectangleContainsScreenPoint(image.rectTransform, screenPoint, camera);
    }

    public void SetBackground(string spec)
    {
        if (!IsReady || _camera == null) throw new InvalidOperationException(L.T("会話画面の準備ができていません。", "The chat stage is not ready."));
        _background.Apply(_camera, spec);
        _settings.BackgroundImage.Value = spec;
        _log.LogInfo($"Conversation background selected: {BackgroundBackdrop.Label(spec)}");
    }

    private void SetupCharacter(int slot = 0)
    {
        var actor = _actors[slot];
        actor.Human.Transform.position = Vector3.zero;
        actor.Human.Transform.rotation = Quaternion.identity;
        actor.Human.Body.LoadAnimation("ani/mot/1/000_00.unity3d", "f_00");
        actor.Animator = actor.Human.Body.animBody;
        actor.Animator.applyRootMotion = false;
        actor.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        PlayMotion(0, slot);
        LayoutActors();
        if (slot != 0) { ApplyGazeMode(); return; }
        var renderers = _human.GameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var top = 0f;
        foreach (var renderer in renderers)
            if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                top = Mathf.Max(top, renderer.bounds.max.y);
        // Accessory bounds can be much taller than the body and otherwise push
        // both the orbit target and the closest camera position away from the face.
        if (top > 0.5f) _height = Mathf.Clamp(top, 14f, 21f);
        foreach (var bone in _human.GameObject.GetComponentsInChildren<Transform>(true))
            if (bone.name.Equals("cf_J_Head", StringComparison.OrdinalIgnoreCase))
            { if (bone.position.y > 1f) _height = bone.position.y * 1.08f; break; }
        _log.LogInfo($"ADV character height={_height}, modelScale={Character.Human.MODEL_SCALE}");
        LayoutActors();
        UpdateCamera();
    }

    private int _idlePose { get => _actors[0].IdlePose; set => _actors[0].IdlePose = value; }
    public int IdlePose => IdlePoseAt(0);
    public int IdlePoseAt(int slot) => _actors[slot].IdlePose >= 0 ? _actors[slot].IdlePose : _actors[slot].Human?.Data?.Parameter?.personality ?? 0;

    public int[] AvailableIdlePoses()
    {
        if (_animator?.runtimeAnimatorController == null) return Array.Empty<int>();
        return Enumerable.Range(0, 12).Where(i => _animator.HasState(0, Animator.StringToHash(MotionCatalog.StateFor(0, i)))).ToArray();
    }

    // Changes the standing pose used for idle; returns false if the state is missing.
    public bool SetIdlePose(int pose, bool play = true, int slot = 0)
    {
        var actor = _actors[slot];
        if (pose >= 0 && actor.Animator != null && !actor.Animator.HasState(0, Animator.StringToHash(MotionCatalog.StateFor(0, pose)))) return false;
        actor.IdlePose = pose;
        if (play && actor.RequestedMotion == 0) PlayMotion(0, slot);
        return true;
    }

    public bool PlayMotion(int motionIndex, int slot = 0)
    {
        var actor = _actors[slot];
        if (actor.Animator?.runtimeAnimatorController == null) return false;
        // Only verified states from the game's controller may be requested.
        var state = MotionCatalog.StateFor(motionIndex, actor.IdlePose >= 0 ? actor.IdlePose : actor.Human?.Data?.Parameter?.personality ?? 0);
        if (state == null) return false;
        var hash = Animator.StringToHash(state);
        if (!actor.Animator.HasState(0, hash)) return false;
        actor.Animator.CrossFadeInFixedTime(hash, 0.15f, 0, 0f);
        actor.RequestedMotion = motionIndex;
        if (motionIndex == 0) ApplyGazeMode();
        _log.LogInfo($"Motion requested: slot={slot}, id={motionIndex}, state={state}, hash={hash}");
        return true;
    }

    // Two characters stand side by side, turned slightly toward each other; one stands in the middle.
    private void LayoutActors()
    {
        var pair = HasPartner && _human != null;
        var offset = _height * .22f;
        for (var slot = 0; slot < _actors.Length; slot++)
        {
            var human = _actors[slot].Human;
            if (human == null) continue;
            var side = pair ? (slot == 0 ? -1f : 1f) : 0f;
            human.Transform.position = new Vector3(side * offset, 0f, 0f);
            human.Transform.rotation = Quaternion.Euler(0f, -side * 10f + _turn, 0f);
        }
    }

    public bool SetPartner(string path)
    {
        if (!IsReady) throw new InvalidOperationException("Conversation stage is not ready.");
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Character card must be a PNG file.");
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Character card not found.", fullPath);
        var data = new HumanData((byte)1);
        if (!data.LoadCharaFile(fullPath)) throw new InvalidDataException("Character card load failed.");
        var replacement = Character.Human.Create(data) ?? throw new InvalidDataException("Character could not be created from card.");
        var previous = _actors[1].Human;
        _actors[1].Human = replacement;
        _actors[1].IdlePose = -1;
        try { SetupCharacter(1); }
        catch
        {
            _actors[1].Human = previous;
            _actors[1].Animator = previous?.Body?.animBody;
            replacement.Dispose();
            LayoutActors();
            throw;
        }
        _actors[1].CardPath = fullPath;
        previous?.Dispose();
        UpdateCamera();
        _log.LogInfo($"ADV second character added: {fullPath}");
        return true;
    }

    public void RemovePartner()
    {
        var human = _actors[1].Human;
        if (human == null) return;
        _actors[1] = new Actor();
        try { human.Dispose(); } catch (Exception ex) { _log.LogWarning($"Second character dispose failed: {ex.Message}"); }
        if (_speaker == 1) _speaker = 0;
        LayoutActors();
        UpdateCamera();
        _log.LogInfo("ADV second character removed.");
    }

    // The two characters change places, so the main slot (personality editor, pose, H lead) is the other one.
    public void SwapPartner()
    {
        if (!HasPartner || _human == null) return;
        (_actors[0], _actors[1]) = (_actors[1], _actors[0]);
        (_activeCardPath, _actors[1].CardPath) = (_actors[0].CardPath, _activeCardPath);
        _speaker = 1 - _speaker;
        LayoutActors();
        ApplyGazeMode();
        _log.LogInfo($"ADV characters swapped: main={_activeCardPath}");
    }

    public int Coordinate => _human == null ? -1 : (int)_human.FileStatus.coordinateType;

    // Swimsuit <-> after-bath through the game's own reload; the rebuilt model needs the
    // conversation's gaze mode and idle motion again.
    public bool SwitchCoordinate()
    {
        if (!IsReady || _human == null) return false;
        var next = Coordinate == 0 ? HumanCoordinate.Define.CoordinateType.AfterBath : HumanCoordinate.Define.CoordinateType.Swimsuit;
        if (!_human.Coorde.ChangeCoordinateTypeAndReload(next, true)) return false;
        PlayMotion(0);
        if (HasPartner && _actors[1].Human.Coorde.ChangeCoordinateTypeAndReload(next, true)) PlayMotion(0, 1);
        ApplyGazeMode();
        _log.LogInfo($"Conversation coordinate switched to {next}.");
        return true;
    }

    public void ToggleEyeLookMode()
    {
        if (!IsReady) return;
        _eyesFollowCamera = !_eyesFollowCamera;
        ApplyGazeMode();
    }

    public void ToggleNeckLookMode()
    {
        if (!IsReady) return;
        _neckFollowsCamera = !_neckFollowsCamera;
        ApplyGazeMode();
    }

    private void ApplyGazeMode()
    {
        foreach (var actor in _actors) ApplyGazeMode(actor.Human);
    }

    private void ApplyGazeMode(Human human)
    {
        if (_camera == null || human?.Face == null) return;
        try
        {
            var face = human.Face;
            var eyes = face.eyeLookCtrl;
            var neck = face.neckLookCtrl;
            var eyePattern = -1;
            var neckPattern = -1;
            if (eyes != null)
            {
                if (_eyesFollowCamera) eyes.Target = _camera.transform;
                var states = eyes.EyeLookScript?.EyeTypeStates;
                if (states != null)
                    for (var i = 0; i < states.Length; i++)
                        if (states[i] != null && states[i].LookType == (_eyesFollowCamera
                            ? EYE_LOOK_TYPE.TARGET : EYE_LOOK_TYPE.NO_LOOK))
                        {
                            eyes.PtnNo = eyePattern = i;
                            break;
                        }
            }
            if (neck != null)
            {
                if (_neckFollowsCamera) neck.Target = _camera.transform;
                var states = neck.NeckLookScript?.neckTypeStates;
                if (states != null)
                    for (var i = 0; i < states.Length; i++)
                        if (states[i] != null && states[i].lookType == (_neckFollowsCamera
                            ? NECK_LOOK_TYPE_VER2.TARGET : NECK_LOOK_TYPE_VER2.ANIMATION))
                        {
                            neck.PtnNo = neckPattern = i;
                            break;
                        }
            }
            _log.LogInfo($"Gaze mode: eyes={(_eyesFollowCamera ? "camera" : "animation")}, neck={(_neckFollowsCamera ? "camera" : "animation")}, eyePattern={eyePattern}, neckPattern={neckPattern}.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Camera gaze setup skipped: {ex}");
        }
    }

    private Transform _hips;

    // Signed yaw (degrees) between the pelvis' facing and the direction to the camera; 0 = facing the camera.
    public float BodyYawToCamera()
    {
        if (_human == null || _camera == null) return float.NaN;
        if (_hips == null || !_hips.IsChildOf(_human.Transform))
            _hips = _human.GameObject.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.Equals("cf_J_Hips", StringComparison.OrdinalIgnoreCase));
        if (_hips == null) return float.NaN;
        var forward = Vector3.ProjectOnPlane(_hips.forward, Vector3.up);
        var toCamera = Vector3.ProjectOnPlane(_camera.transform.position - _hips.position, Vector3.up);
        return Vector3.SignedAngle(toCamera, forward, Vector3.up);
    }

    public object Diagnostics()
    {
        if (!IsReady || _animator == null) return new { ready = false, status = Status };
        var state = _animator.GetCurrentAnimatorStateInfo(0);
        var bones = new Dictionary<string, object>();
        var renderers = _human.GameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var sampled = 0;
        foreach (var renderer in renderers)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            foreach (var t in renderer.bones)
            {
                if (t == null || bones.ContainsKey(t.name)) continue;
                var p = t.position;
                var q = t.rotation;
                bones[t.name] = new { position = new[] { p.x, p.y, p.z }, rotation = new[] { q.x, q.y, q.z, q.w } };
                if (++sampled >= 40) break;
            }
            if (sampled >= 40) break;
        }
        return new { ready = true, character = CharacterName,
            cardId = Path.GetFileNameWithoutExtension(_activeCardPath), personality = _human.Data.Parameter.personality,
            requestedMotion = _requestedMotion, bodyYaw = BodyYawToCamera(), rootYaw = _human.Transform.eulerAngles.y,
            eyesFollowCamera = _eyesFollowCamera, neckFollowsCamera = _neckFollowsCamera,
            eyePattern = _human.Face?.eyeLookCtrl?.PtnNo ?? -1,
            neckPattern = _human.Face?.neckLookCtrl?.PtnNo ?? -1,
            stateHash = state.fullPathHash, expectedHash = Animator.StringToHash(MotionCatalog.StateFor(_requestedMotion, _human.Data.Parameter.personality)),
            normalizedTime = state.normalizedTime, inTransition = _animator.IsInTransition(0),
            camera = _camera == null ? null : new { distance = _distance, yaw = _yaw, pitch = _pitch,
                targetHeight = _targetHeight, fov = _camera.fieldOfView, height = _height,
                position = new[] { _camera.transform.position.x, _camera.transform.position.y, _camera.transform.position.z } },
            bones, text = _lastText,
            background = new { spec = _background.CurrentSpec, visible = _background.Visible },
            postProcessing = PostProcessingDiagnostics() };
    }

    public void SelectCard(int direction)
    {
        if (!IsReady || _cards.Length == 0) return;
        var next = _cardIndex < 0
            ? (direction >= 0 ? 0 : _cards.Length - 1)
            : (_cardIndex + direction + _cards.Length) % _cards.Length;
        SelectCard(_cards[next]);
    }

    public void SelectCard(string path)
    {
        if (!IsReady) throw new InvalidOperationException("Conversation stage is not ready.");
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Character card must be a PNG file.");
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Character card not found.", fullPath);
        var data = new HumanData((byte)1);
        if (!data.LoadCharaFile(fullPath)) throw new InvalidDataException("Character card load failed.");
        var replacement = Character.Human.Create(data);
        if (replacement == null) throw new InvalidDataException("Character could not be created from card.");
        var previous = _human;
        _human = replacement;
        try { SetupCharacter(); }
        catch
        {
            _human = previous;
            _animator = previous?.Body?.animBody;
            replacement.Dispose();
            throw;
        }
        _cardIndex = Array.FindIndex(_cards, card => string.Equals(card, fullPath, StringComparison.OrdinalIgnoreCase));
        _activeCardPath = fullPath;
        previous?.Dispose();
        ShowText("……");
        _log.LogInfo($"ADV character selected: {fullPath}");
    }

    private float _turn;

    public void Rotate(float degrees)
    {
        _turn = Mathf.Repeat(_turn + degrees + 180f, 360f) - 180f;
        LayoutActors();
    }

    public void Zoom(float steps)
    {
        _distance = Mathf.Clamp(_distance * Mathf.Pow(1.25f, steps), MinimumDistance, MaximumDistance);
        UpdateCamera();
    }

    public void UpdateView(bool typing)
    {
        if (!IsReady) return;
        if (typing) { _dragButton = -1; RestoreCursor(); UpdateCamera(); return; }
        var unlocked = ModSettings.Held(_settings.UnlockViewPrimary.Value)
            || ModSettings.Held(_settings.UnlockViewSecondary.Value);
        if (unlocked && !_viewUnlocked)
        {
            _oldCursorLock = Cursor.lockState;
            _oldCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _viewUnlocked = true;
        }
        if (!unlocked) RestoreCursor();
        if (!typing && (ModSettings.Pressed(_settings.ResetViewPrimary.Value)
            || ModSettings.Pressed(_settings.ResetViewSecondary.Value)))
        {
            ResetView();
            return;
        }
        if (!unlocked) { _dragButton = -1; UpdateCamera(); return; }
        var overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        if (_dragButton >= 0 && !Input.GetMouseButton(_dragButton)) _dragButton = -1;
        if (_dragButton < 0 && !overUi)
            foreach (var button in new[] { _settings.OrbitMouseButton.Value, _settings.PanMouseButton.Value, _settings.AlternatePanMouseButton.Value })
                if (button is >= 0 and <= 2 && Input.GetMouseButtonDown(button)) { _dragButton = button; break; }
        var x = Input.GetAxis("Mouse X");
        var y = Input.GetAxis("Mouse Y");
        var nativeCamera = Manager.Config.CameraData;
        if (nativeCamera != null)
        {
            x *= Mathf.Clamp(nativeCamera.SensitivityX * 2f, .02f, 4f) * (nativeCamera.InvertMoveX ? -1f : 1f);
            y *= Mathf.Clamp(nativeCamera.SensitivityY * 2f, .02f, 4f) * (nativeCamera.InvertMoveY ? -1f : 1f);
        }
        if (_dragButton >= 0 && _dragButton == _settings.OrbitMouseButton.Value)
        {
            var sensitivity = Mathf.Clamp(_settings.OrbitSensitivity.Value, 0.1f, 10f);
            _yaw += x * sensitivity;
            _pitch = Mathf.Clamp(_pitch - y * sensitivity, -65f, 65f);
        }
        else if (_dragButton >= 0)
        {
            var sensitivity = Mathf.Clamp(_settings.PanSensitivity.Value, 0.001f, 0.05f);
            _pan += (_camera.transform.right * -x + _camera.transform.up * -y) * _height * sensitivity;
        }
        if (!overUi) _distance = Mathf.Clamp(_distance * Mathf.Pow(1.25f, -Input.mouseScrollDelta.y), MinimumDistance, MaximumDistance);
        UpdateCamera();
    }

    public void ResetView()
    {
        SetCameraPreset(_settings.CameraPreset.Value);
    }

    public void SetCameraPreset(string preset)
    {
        (_distance, _targetHeight) = preset switch
        {
            "face" => (.30f, .89f),
            "full" => (1.95f, .50f),
            _ => (.75f, .84f)
        };
        _yaw = 0f;
        _pitch = 3f;
        _pan = Vector3.zero;
        UpdateCamera();
    }

    private void RestoreCursor()
    {
        if (!_viewUnlocked) return;
        Cursor.lockState = _oldCursorLock;
        Cursor.visible = _oldCursorVisible;
        _viewUnlocked = false;
    }

    public static readonly (float Vertical, float Horizontal, float Intensity, string Color) LightDefaults = (25f, -30f, 1f, "#DFE6E6FF");

    public void ApplyLightSettings()
    {
        var light = _light?.GetComponent<Light>();
        if (light == null) return;
        light.intensity = Mathf.Clamp(_settings.LightIntensity.Value, 0f, 3f);
        light.color = ColorUtility.TryParseHtmlString(_settings.LightColor.Value, out var color) ? color : new Color(0.873f, 0.901f, 0.901f);
        _light.transform.localRotation = Quaternion.Euler(Mathf.Clamp(_settings.LightVertical.Value, -90f, 90f),
            Mathf.Repeat(_settings.LightHorizontal.Value + 180f, 360f) - 180f, 0f);
    }

    public Color LightColor => _light?.GetComponent<Light>()?.color ?? Color.white;

    public void RefreshGraphics()
    {
        if (_camera != null)
        {
            var extra = _camera.GetComponent<UniversalAdditionalCameraData>();
            if (extra != null)
            {
                if (!_cameraGraphicsCaptured)
                {
                    _oldPostProcessing = extra.renderPostProcessing;
                    _oldVolumeMask = extra.volumeLayerMask;
                    _oldVolumeTrigger = extra.volumeTrigger;
                    _oldAntialiasing = extra.antialiasing;
                    _cameraGraphicsCaptured = true;
                }
                _postProcessing.Ensure(_camera, _log);
                _postProcessing.Refresh(_camera);
            }
            if (Manager.Config.GraphicData != null) _camera.backgroundColor = Manager.Config.GraphicData.BackColor;
        }
        if (_light != null) _light.GetComponent<Light>().shadows = AiChatBehaviour.Graphics.ShadowType.Value;
        GraphicPreferences.Refresh();
    }

    private object PostProcessingDiagnostics()
    {
        var extra = _camera == null ? null : _camera.GetComponent<UniversalAdditionalCameraData>();
        var volumes = UnityEngine.Object.FindObjectsOfType<Volume>(true);
        return new { enabled = extra?.renderPostProcessing == true,
            mask = extra == null ? 0 : extra.volumeLayerMask.value,
            applied = _postProcessing.Diagnostics(),
            volumes = volumes.Take(24).Select(v => new { name = v.name,
                active = v.isActiveAndEnabled, layer = v.gameObject.layer,
                global = v.isGlobal, weight = v.weight,
                components = VolumeComponentNames(v) }).ToArray() };
    }

    private static string[] VolumeComponentNames(Volume volume)
    {
        if (volume.profileRef == null) return Array.Empty<string>();
        var components = volume.profileRef.components;
        var result = new string[components.Count];
        for (var i = 0; i < components.Count; i++) result[i] = components[i]?.name ?? "null";
        return result;
    }

    private void UpdateCamera(bool snap = false)
    {
        if (_camera == null) return;
        var target = new Vector3(0f, _height * _targetHeight, 0f) + _pan;
        var width = HasPartner && _human != null ? 1.35f : 1f;
        var position = target + Quaternion.Euler(-_pitch, _yaw, 0f) * new Vector3(0f, 0f, _height * _distance * width);
        var rotation = Quaternion.LookRotation(target - position);
        var smoothing = Mathf.Clamp(_settings.CameraSmoothing.Value, 0f, .5f);
        var t = snap || !_cameraInitialized || smoothing <= 0f ? 1f : 1f - Mathf.Exp(-Time.unscaledDeltaTime / smoothing);
        _camera.transform.position = Vector3.Lerp(_camera.transform.position, position, t);
        _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation, rotation, t);
        _cameraInitialized = true;
        _background.RefreshFrame();
    }

    // Hides the stage while another scene (H) owns the screen, without releasing it.
    public bool Suspended { get; private set; }

    public void Suspend()
    {
        if (!IsReady || Suspended) return;
        Suspended = true;
        foreach (var actor in _actors) actor.Human?.GameObject.SetActive(false);
        if (_camera != null) _camera.enabled = false;
        if (_light != null) { RenderSettings.sun = _oldSun; _light.SetActive(false); }
        if (_nativeCanvasGroup != null) { _nativeCanvasGroup.alpha = 0f; _nativeCanvasGroup.blocksRaycasts = false; _nativeCanvasGroup.interactable = false; }
        _uiRoot.SetActive(false);
        // ADVSetup.Load registers the ADV core as a scene overlap, and every CameraController
        // ignores input while one exists, so the H scene's camera could not be moved.
        if (_core != null) Manager.Scene.Remove(new Manager.Scene.IOverlap(_core.Pointer));
        _postProcessing.SetActive(false);
        AiChatBehaviour.Graphics.Exit();
        RestoreCursor();
    }

    public void Resume()
    {
        if (!Suspended) return;
        Suspended = false;
        _uiRoot.SetActive(true);
        if (_core != null) Manager.Scene.Add(new Manager.Scene.IOverlap(_core.Pointer));
        AiChatBehaviour.Graphics.Enter();
        _postProcessing.SetActive(true);
        foreach (var actor in _actors) actor.Human?.GameObject.SetActive(true);
        if (_camera != null) _camera.enabled = true;
        if (_light != null) { _light.SetActive(true); RenderSettings.sun = _light.GetComponent<Light>(); }
        if (_nativeCanvasGroup != null) { _nativeCanvasGroup.alpha = 1f; _nativeCanvasGroup.blocksRaycasts = true; _nativeCanvasGroup.interactable = true; }
        PlayMotion(_requestedMotion);
        if (HasPartner) PlayMotion(_actors[1].RequestedMotion, 1);
        RefreshGraphics();
    }

    public void Exit()
    {
        Suspended = false;
        _requested = false;
        AiChatBehaviour.Graphics.Exit();
        _postProcessing.Dispose();
        if (_cameraGraphicsCaptured && _camera != null)
        {
            var extra = _camera.GetComponent<UniversalAdditionalCameraData>();
            if (extra != null)
            {
                extra.renderPostProcessing = _oldPostProcessing;
                extra.volumeLayerMask = _oldVolumeMask;
                extra.volumeTrigger = _oldVolumeTrigger;
                extra.antialiasing = _oldAntialiasing;
            }
        }
        _cameraGraphicsCaptured = false;
        _cameraInitialized = false;
        _dragButton = -1;
        RestoreCursor();
        _dialogueFrame.Restore();
        _background.Dispose();
        if (_core?.Scenario != null) _core.Scenario.VisibleWindowForce = false;
        if (_nativeCanvasGroup != null)
        {
            _nativeCanvasGroup.alpha = 0f;
            _nativeCanvasGroup.blocksRaycasts = false;
            _nativeCanvasGroup.interactable = false;
        }
        if (_camera != null) _camera.enabled = false;
        if (_core != null) _core.gameObject.SetActive(false);
        _ready = false;
        _activeCardPath = null;
        if (_human != null) { _human.Dispose(); _human = null; }
        if (_actors[1].Human != null) try { _actors[1].Human.Dispose(); } catch (Exception ex) { _log.LogWarning($"Second character dispose failed: {ex.Message}"); }
        _actors[0] = new Actor();
        _actors[1] = new Actor();
        _turn = 0f;
        _moreTurns = false;
        if (_light != null) { RenderSettings.sun = _oldSun; UnityEngine.Object.Destroy(_light); _light = null; }
        foreach (var state in _titleGroups)
        {
            if (state.Group == null) continue;
            if (state.Added) UnityEngine.Object.Destroy(state.Group);
            else { state.Group.alpha = state.Alpha; state.Group.interactable = state.Interactable; state.Group.blocksRaycasts = state.Raycasts; }
        }
        _titleGroups.Clear();
        if (_loaded) ADVSetup.Dispose();
        _core = null;
        _nativeCanvas = null;
        _nativeCanvasGroup = null;
        _loaded = false;
    }

    public void Dispose() => Exit();
}
