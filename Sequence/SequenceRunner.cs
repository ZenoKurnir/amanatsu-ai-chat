using Amanatsu.AiChat.GameAdapter;
using BepInEx.Logging;
using UnityEngine;

namespace Amanatsu.AiChat.Sequence;

internal sealed class SequenceRunner
{
    private readonly Queue<SequenceCommand> _queue = new();
    private readonly Func<int, CharacterAdapter> _characters;
    private readonly ManualLogSource _log;
    private readonly Action<string, int, bool> _showText;
    private readonly Func<bool> _readerPending;
    private float _resumeAt;
    private bool _wasRunning;
    private bool _waitForReader;
    private readonly HashSet<int> _activeMotions = new();
    private float _lastMotionAt;
    private bool _paused;
    private float _pausedAt;

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        if (paused) _pausedAt = Time.realtimeSinceStartup;
        else { var elapsed = Time.realtimeSinceStartup - _pausedAt; if (_resumeAt > 0) _resumeAt += elapsed; _lastMotionAt += elapsed; _readerDeadline += elapsed; }
        _paused = paused;
    }

    // characters: the adapter for a target slot; showText(text, speaker, moreTurns) shows a line, and with
    // moreTurns the runner waits until readerPending() is false (the player turned to the next speaker).
    public SequenceRunner(Func<int, CharacterAdapter> characters, Action<string, int, bool> showText,
        Func<bool> readerPending, ManualLogSource log)
    {
        _characters = characters;
        _showText = showText;
        _readerPending = readerPending;
        _log = log;
    }

    public bool IsRunning => _queue.Count > 0 || _resumeAt > 0 || _waitForReader;
    public bool WaitingForReader => _waitForReader;
    // A turn that is never clicked moves on by itself after a reading time, so a reply cannot stall.
    private float _readerDeadline;

    public void Start(IEnumerable<SequenceCommand> commands)
    {
        Cancel();
        foreach (var command in commands)
            _queue.Enqueue(command);
        _wasRunning = _queue.Count > 0;
    }

    public void Cancel()
    {
        _queue.Clear();
        _resumeAt = 0;
        _wasRunning = false;
        _waitForReader = false;
        foreach (var target in _activeMotions) _characters(target)?.PlayMotion("idle");
        _activeMotions.Clear();
    }

    public void Update()
    {
        if (_paused) return;
        if (_waitForReader)
        {
            if (_readerPending() && Time.realtimeSinceStartup < _readerDeadline) return;
            _waitForReader = false;
        }
        if (_resumeAt > 0)
        {
            if (Time.realtimeSinceStartup < _resumeAt)
                return;
            _resumeAt = 0;
        }

        for (var budget = 0; budget < 12 && _queue.Count > 0; budget++)
        {
            var command = _queue.Dequeue();
            var target = command.Target is 1 ? 1 : 0;
            var _character = _characters(target) ?? _characters(0);
            switch (command.Type)
            {
                case "expression":
                    if (!_character.SetExpression(command.Value!))
                        _log.LogWarning($"Expression could not be applied: {command.Value}");
                    break;
                case "eyebrow":
                case "eyes":
                case "mouth":
                    if (!_character.SetFacePart(command.Type, int.Parse(command.Value!)))
                        _log.LogWarning($"Face part could not be applied: {command.Type}={command.Value}");
                    break;
                case "outfit":
                    if (!_character.SetOutfit(command.Value!))
                        _log.LogWarning($"Outfit could not be applied: {command.Value}");
                    break;
                case "pose":
                    if (!_character.SetPose(command.Value!))
                        _log.LogWarning($"Pose could not be applied: {command.Value}");
                    break;
                case "motion":
                    if (!_character.PlayMotion(command.Value!))
                        _log.LogWarning($"Motion could not be applied: {command.Value}");
                    else if (string.Equals(command.Value, "idle", StringComparison.OrdinalIgnoreCase))
                        _activeMotions.Remove(target);
                    else
                    {
                        _activeMotions.Add(target);
                        _lastMotionAt = Time.realtimeSinceStartup;
                    }
                    break;
                case "text":
                    // In a two-character reply (commands carry a target) another line follows: show this
                    // one until the player turns the page. One-character sequences keep their timing.
                    var more = command.Target != null && _queue.Any(next => next.Type == "text");
                    _showText(command.Value!, target, more);
                    if (more)
                    {
                        _waitForReader = true;
                        _readerDeadline = Time.realtimeSinceStartup + Math.Clamp(command.Value!.Length * 0.08f, 4f, 12f);
                        return;
                    }
                    break;
                case "wait":
                    _resumeAt = Time.realtimeSinceStartup + command.Duration!.Value;
                    return;
            }
        }

        if (_wasRunning && !IsRunning)
        {
            if (_activeMotions.Count > 0 && Time.realtimeSinceStartup < _lastMotionAt + 1.5f)
            {
                _resumeAt = _lastMotionAt + 1.5f;
                return;
            }
            foreach (var target in _activeMotions) _characters(target)?.PlayMotion("idle");
            _activeMotions.Clear();
            _wasRunning = false;
            _log.LogInfo("Sequence completed.");
        }
    }
}
