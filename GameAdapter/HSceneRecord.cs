using System.Text;

namespace Amanatsu.AiChat.GameAdapter;

// What happened in one H scene, read from the game's motion states while it runs, so the girls can
// remember it afterwards. Each posture names who is in it: a one-girl posture belongs to the girl the game
// has active, a posture with both girls to both. When both are in it and the game does not say which
// girl a climax was (a 3P climax), it is kept apart as shared.
internal sealed class HSceneRecord
{
    public bool GirlsOnly { get; init; }
    public string Place { get; init; }

    private sealed class Tally
    {
        public readonly List<string> Activities = new();
        public readonly Dictionary<string, int> Climaxes = new();
        public int Together, WithOtherGirl;
        public int Inside, InMouth, OnFace, OnBody, Bukkake, Swallowed, Spat;
        public bool Tired;
    }

    private const string Shared = "";
    // Per girl name; Shared holds what happened in a both-girls posture without saying to whom.
    private readonly Dictionary<string, Tally> _tallies = new();
    private string[] _girls = Array.Empty<string>();
    private string _category = "";
    private bool _anal;
    private string _family;
    private string _postureName = "";
    private bool _location = true;
    private bool _intercourse;
    private string _spot = "";
    // Spots of the map used, in order (Floor, Chair, Desk, BeachChair, Poolside, Pool, Sea).
    private readonly List<string> _spots = new();

    public void OnSpot(string spot)
    {
        _spot = spot ?? "";
        if (_spot.Length > 0 && !_spots.Contains(_spot)) _spots.Add(_spot);
    }

    private static string SpotText(string spot) => spot switch
    {
        // The plain ground is not worth mentioning; only spots that set a scene are.
        "Floor" => null,
        "Chair" => "on a chair",
        "Desk" => "on a table",
        "BeachChair" => "on a beach lounger",
        "Poolside" => "at the edge of the pool",
        "Pool" => "in the water of the pool",
        "Sea" => "in the sea",
        _ => null
    };

    // category: Caress, Service, InsertM, InsertF, M1F2, Les, Special; girls: the girls the posture uses;
    // anal: the posture is an anal one (the game marks it in the posture name); name: the game's posture name.
    public void OnPosture(string category, string[] girls, bool anal, string name = null)
    {
        _category = category ?? "";
        _girls = girls ?? Array.Empty<string>();
        _anal = anal;
        _postureName = name ?? "";
        // Penetrative sex, as opposed to only caressing or service.
        if (_category is "InsertM" or "InsertF" || _category == "M1F2" && (_postureName.Contains("位") || _postureName.Contains("バック")))
            _intercourse = true;
        var activity = $"{_category}:{(anal ? "anal" : "")}:{_postureName}";
        foreach (var girl in _girls)
        {
            var tally = TallyOf(girl);
            if (!tally.Activities.Contains(activity)) tally.Activities.Add(activity);
        }
    }

    // Called with the controller's state class name (OrgasmF, SOrgasmM_IN_ST, Drink_A, ...) on every change.
    // A state and its _ST/_A/_IN steps form one event; the S prefix is the faster loop of the same state.
    public void OnState(string name)
    {
        if (string.IsNullOrEmpty(name) || _girls.Length == 0) return;
        if (name.Length > 1 && name[0] == 'S' && char.IsUpper(name[1]) && !name.StartsWith("SSP")) name = name[1..];
        var family = name.StartsWith("OrgasmM_IN") ? "OrgasmM_IN"
            : name.StartsWith("OrgasmM_OUT") ? "OrgasmM_OUT"
            : name.StartsWith("OrgasmF") ? "OrgasmF"
            : name.StartsWith("OrgasmS") ? "OrgasmS"
            : name.StartsWith("Drink") ? "Drink"
            : name.StartsWith("Vomit") ? "Vomit"
            : name;
        if (family == _family) return;
        _family = family;
        var activity = $"{_category}:{(_anal ? "anal" : "")}:{_postureName}";
        // In girl-girl play a simultaneous climax is both girls at once, so each one is known.
        if (family == "OrgasmS" && _category == "Les")
        {
            foreach (var girl in _girls) { var each = TallyOf(girl); Add(each, activity); each.WithOtherGirl++; }
            return;
        }
        var tally = TallyOf(_girls.Length == 1 ? _girls[0] : Shared);
        switch (family)
        {
            case "OrgasmF": Add(tally, activity); break;
            case "OrgasmS": Add(tally, activity); tally.Together++; PlayerCame(tally); break;
            case "OrgasmM_IN": PlayerCame(tally); break;
            case "OrgasmM_OUT" when _category == "Service": tally.OnFace++; break;
            case "OrgasmM_OUT": tally.OnBody++; break;
            // The service option where the player finishes over the girl (bukkake).
            case "Put": tally.Bukkake++; break;
            case "Drink": tally.Swallowed++; break;
            case "Vomit": tally.Spat++; break;
        }
    }

    // The game's exhausted state, seen while a posture runs: it belongs to that posture's girl.
    public void OnTired() => TallyOf(_girls.Length == 1 ? _girls[0] : Shared).Tired = true;

    private Tally TallyOf(string girl) => _tallies.TryGetValue(girl, out var tally) ? tally : _tallies[girl] = new Tally();

    private static void Add(Tally tally, string activity) => tally.Climaxes[activity] = tally.Climaxes.GetValueOrDefault(activity) + 1;

    private void PlayerCame(Tally tally)
    {
        if (_category == "Service") tally.InMouth++;
        else tally.Inside++;
    }

    // The posture's own name when it can be read (kissing, doggy style, titjob...), otherwise its category.
    private string Activity(string activity, string her)
    {
        var parts = activity.Split(':', 3);
        var named = parts.Length == 3 ? PostureText.Describe(parts[2], parts[0] is "Caress" or "Les", _location) : null;
        if (named != null) return named;
        var anal = parts.Length > 1 && parts[1] == "anal";
        return parts[0] switch
        {
            "Caress" => "kissing and caressing",
            "Service" => "oral, hand or breast service for the player",
            "InsertM" => anal ? "anal sex with the player leading" : "sex with the player leading",
            "InsertF" => anal ? $"anal sex with {her} on top" : $"sex with {her} on top",
            "M1F2" => "a threesome position with both girls",
            "Les" => "the girls pleasuring each other",
            _ => "a special position"
        };
    }

    private static string Times(int n) => n == 1 ? "once" : n == 2 ? "twice" : $"{n} times";

    // girls: the girls on stage. Anyone who never appeared in a posture is described as watching.
    // location: include the map and the spots; off for players who imagine their own setting.
    public string Describe(string[] girls, bool location = true)
    {
        _location = location;
        // The scene names girls by their card's full name; the chat may use a shorter one.
        string KeyOf(string girl) => _tallies.Keys.FirstOrDefault(key => key != Shared
            && (key == girl || key.EndsWith(" " + girl) || girl.EndsWith(" " + key)));
        var took = girls.Where(girl => KeyOf(girl) != null).ToArray();
        var watched = girls.Where(girl => KeyOf(girl) == null).ToArray();
        var who = string.Join(" and ", took);
        var text = new StringBuilder();
        if (took.Length == 0) text.Append("The player started an intimate scene, but nothing happened");
        else text.Append(GirlsOnly
            ? $"{who} just had sex with each other while the player watched"
            : took.Length > 1 ? $"{who} just {(_intercourse ? "had a threesome" : "got intimate together")} with the player"
            : $"{who} just {(_intercourse ? "had sex" : "got intimate")} with the player");
        if (location && !string.IsNullOrWhiteSpace(Place)) text.Append($" at the {Place}");
        text.Append('.');
        var spots = location ? _spots.Select(SpotText).Where(spot => spot != null).ToList() : new List<string>();
        // Spots left unsaid (the ground) mean only part of it happened at the named ones.
        var partly = location && _spots.Any(spot => SpotText(spot) == null);
        if (spots.Count == 1) text.Append(partly ? $" Part of it happened {spots[0]}." : $" It happened {spots[0]}.");
        else if (spots.Count > 1) text.Append($" Part of it happened {string.Join(", then ", spots)}.");
        foreach (var girl in took) DescribeTally(text, girl, _tallies[KeyOf(girl)], "she", "her");
        if (_tallies.TryGetValue(Shared, out var shared))
            DescribeTally(text, "In the positions with both girls, one of them (the game does not say which)", shared, "she", "her", true);
        foreach (var girl in watched)
            text.Append($" {girl} was there watching the whole time but did not take part.");
        return text.ToString();
    }

    private void DescribeTally(StringBuilder text, string name, Tally tally, string she, string her, bool shared = false)
    {
        var lines = new List<string>();
        if (!shared && tally.Activities.Count > 0)
            lines.Add($"What {name} did: {string.Join("; ", tally.Activities.Select(activity => Activity(activity, her)))}");
        var climaxes = tally.Climaxes.Values.Sum();
        if (climaxes > 0)
            lines.Add($"{(shared ? name : she)} climaxed {Times(climaxes)} ({string.Join("; ", tally.Climaxes.OrderByDescending(pair => pair.Value).Select(pair => $"{Times(pair.Value)} during {Activity(pair.Key, her)}"))})");
        // A climax in a both-girls posture may have been hers, so "did not climax" would be wrong then.
        else if (!shared && !(_tallies.TryGetValue(Shared, out var both) && both.Climaxes.Count > 0)) lines.Add($"{she} did not climax");
        if (tally.WithOtherGirl > 0) lines.Add($"{she} climaxed together with the other girl {Times(tally.WithOtherGirl)}");
        if (tally.Together > 0) lines.Add($"{she} came at the same moment as the player {Times(tally.Together)}");
        if (!GirlsOnly)
        {
            var player = new List<string>();
            if (tally.Inside > 0) player.Add($"came inside {her} {Times(tally.Inside)}");
            if (tally.InMouth > 0) player.Add($"came in {her} mouth {Times(tally.InMouth)}");
            if (tally.OnFace > 0) player.Add($"pulled out of {her} mouth and came on {her} face {Times(tally.OnFace)}");
            if (tally.OnBody > 0) player.Add($"pulled out and came on {her} body {Times(tally.OnBody)}");
            if (tally.Bukkake > 0) player.Add($"finished all over {her} face and body {Times(tally.Bukkake)}");
            if (player.Count > 0) lines.Add($"the player {string.Join("; ", player)}");
            if (tally.Swallowed > 0) lines.Add($"{she} swallowed his cum {Times(tally.Swallowed)}");
            if (tally.Spat > 0) lines.Add($"{she} spat his cum out {Times(tally.Spat)}");
        }
        if (tally.Tired) lines.Add($"by the end {she} was completely worn out from climaxing so much");
        if (lines.Count == 0) return;
        var sentence = string.Join(". ", lines.Select(line => char.ToUpper(line[0]) + line[1..]));
        text.Append(shared || lines[0].Contains(name) ? $" {sentence}." : $" {name}: {sentence}.");
    }
}
