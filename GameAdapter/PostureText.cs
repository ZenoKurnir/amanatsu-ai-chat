namespace Amanatsu.AiChat.GameAdapter;

// Turns the game's Japanese posture names (床キス, 立ちバック, 椅子パイズリ舐め, ...) into a plain English
// description for the girls' memory, by the words the names are built from. caress: the posture is in the
// caress category, where licking and touching are done to her; elsewhere she does them to the player.
internal static class PostureText
{
    private enum Kind { Act, Modifier, Place }

    // Longer words first, so 背面騎乗位 is read before 騎乗位 and 立ちバック before バック.
    private static readonly (string Word, Kind Kind, string Caress, string Service)[] Words =
    {
        ("貝合わせ", Kind.Act, "scissoring (rubbing their pussies together)", null),
        ("シックスナイン", Kind.Act, "69", null),
        ("顔面騎乗", Kind.Act, "her sitting on his face", null),
        ("背面騎乗位", Kind.Act, "reverse cowgirl (her on top, facing away)", null),
        ("騎乗位", Kind.Act, "cowgirl (her on top)", null),
        ("逆駅弁", Kind.Act, "him carrying her in his arms facing away", null),
        ("駅弁", Kind.Act, "him carrying her in his arms", null),
        ("対面座位", Kind.Act, "sitting face to face in his lap", null),
        ("背面座位", Kind.Act, "sitting in his lap facing away", null),
        ("座位", Kind.Act, "sitting in his lap", null),
        ("対面立位", Kind.Act, "standing face to face", null),
        ("背面立位", Kind.Act, "standing doggy style", null),
        ("立位", Kind.Act, "standing sex", null),
        ("正常位", Kind.Act, "missionary", null),
        ("後背位", Kind.Act, "doggy style", null),
        ("寝バック", Kind.Act, "doggy style lying flat", null),
        ("立ちバック", Kind.Act, "standing doggy style", null),
        ("バック", Kind.Act, "doggy style", null),
        ("側位", Kind.Act, "lying on their sides", null),
        ("屈脚位", Kind.Act, "with her legs bent back", null),
        ("逆挿入", Kind.Act, "upside-down penetration", null),
        ("ピストン", Kind.Act, "thrusting", null),
        ("相互愛撫", Kind.Act, "caressing each other", null),
        ("愛撫", Kind.Act, "him caressing her", null),
        ("キス", Kind.Act, "kissing", null),
        ("胸もみ", Kind.Act, "him fondling her breasts", null),
        ("胸揉み", Kind.Act, "him fondling her breasts", null),
        ("胸弄り", Kind.Act, "him playing with her breasts", null),
        ("乳首舐め", Kind.Act, "him licking her nipples", "her licking his nipples"),
        ("乳首弄り", Kind.Act, "him playing with her nipples", null),
        ("手マン", Kind.Act, "him fingering her", null),
        ("Gスポット", Kind.Act, "him stimulating her G-spot", null),
        ("股間弄り", Kind.Act, "him touching her between the legs", null),
        ("クンニ", Kind.Act, "him licking her pussy", null),
        ("アナル舐め", Kind.Act, "him licking her ass", "her licking his ass"),
        ("アナル弄り", Kind.Act, "him playing with her ass", null),
        ("アナルバイブ", Kind.Act, "an anal vibrator", null),
        ("先舐め", Kind.Act, "her licking the tip of his cock", null),
        ("イラマ", Kind.Act, "him thrusting deep into her throat", null),
        ("ディープスロート", Kind.Act, "him thrusting deep into her throat", null),
        ("フェラ", Kind.Act, "blowjob", null),
        ("咥え", Kind.Act, "her sucking his cock", null),
        ("パイズリ", Kind.Act, "titjob", null),
        ("手コキ", Kind.Act, "handjob", null),
        ("足コキ", Kind.Act, "footjob", null),
        ("足愛撫", Kind.Act, "her caressing him with her feet", null),
        ("素股", Kind.Act, "rubbing his cock between her thighs without penetration", null),
        ("亀頭弄り", Kind.Act, "her teasing the head of his cock", null),
        ("搾精", Kind.Act, "her milking him", null),
        ("洗体", Kind.Act, "her washing him with her body", null),
        ("舐め", Kind.Act, "him licking her body", "her licking his cock"),
        ("オナニー", Kind.Act, "masturbation", null),
        ("ディルド", Kind.Act, "a dildo", null),
        ("バイブ", Kind.Act, "a vibrator", null),
        ("三角木馬", Kind.Act, "a wooden horse", null),
        ("触手", Kind.Act, "tentacles", null),
        ("挿入", Kind.Act, "penetration", null),
        ("アナル", Kind.Modifier, "anal", null),
        ("男拘束", Kind.Modifier, "with him tied up", null),
        ("拘束", Kind.Modifier, "with her tied up", null),
        ("手つなぎ", Kind.Modifier, "holding hands", null),
        ("抱きつき", Kind.Modifier, "hugging", null),
        ("四つん這い", Kind.Modifier, "on all fours", null),
        ("しゃがみ", Kind.Modifier, "crouching", null),
        ("中腰", Kind.Modifier, "half standing", null),
        ("ガニ股", Kind.Modifier, "legs spread wide", null),
        ("開脚", Kind.Modifier, "legs spread", null),
        ("足掛け", Kind.Modifier, "one leg raised", null),
        ("まんぐり", Kind.Modifier, "her legs pulled back over her head", null),
        ("マングリ", Kind.Modifier, "her legs pulled back over her head", null),
        ("チングリ", Kind.Modifier, "his legs pulled back over his head", null),
        ("添い寝", Kind.Modifier, "lying together", null),
        ("寝", Kind.Modifier, "lying down", null),
        ("立ち", Kind.Modifier, "standing", null),
        ("脱力", Kind.Modifier, "while she was exhausted", null),
        ("プールサイド", Kind.Place, "by the pool", null),
        ("プール内", Kind.Place, "in the pool", null),
        ("水中", Kind.Place, "in the water", null),
        ("ビーチチェア", Kind.Place, "on a beach lounger", null),
        ("浮き輪", Kind.Place, "on a swim ring", null),
        ("壁穴", Kind.Place, "through a hole in the wall", null),
        ("壁", Kind.Place, "against a wall", null),
        ("椅子", Kind.Place, "on a chair", null),
        ("ソファー", Kind.Place, "on the sofa", null),
        ("ソファ", Kind.Place, "on the sofa", null),
        ("ベンチ", Kind.Place, "on a bench", null),
        ("机下", Kind.Place, "under the table", null),
        ("机", Kind.Place, "on a table", null),
        ("卓上", Kind.Place, "on a table", null),
        // The floor is where most postures happen anyway; saying so sounds unnatural.
        ("床", Kind.Place, null, null),
        ("バスタブ", Kind.Place, "in the bathtub", null),
        ("便座", Kind.Place, "on the toilet", null),
        ("トイレ", Kind.Place, "on the toilet", null),
        ("階段", Kind.Place, "on the stairs", null),
        ("本棚", Kind.Place, "by the bookshelf", null),
    };

    // null when no known word is in the name; location: keep the place words (floor, chair, pool...).
    public static string Describe(string name, bool caress, bool location)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var rest = name;
        var acts = new List<string>();
        var modifiers = new List<string>();
        var places = new List<string>();
        var both = rest.StartsWith("W");
        foreach (var (word, kind, caressText, serviceText) in Words.OrderByDescending(entry => entry.Word.Length))
        {
            if (!rest.Contains(word)) continue;
            rest = rest.Replace(word, "|");
            var text = !caress && serviceText != null ? serviceText : caressText;
            var list = kind == Kind.Act ? acts : kind == Kind.Modifier ? modifiers : places;
            if (text != null && !list.Contains(text)) list.Add(text);
        }
        if (acts.Count == 0) return null;
        // The anal mark reads better inside the act: "anal missionary" rather than "missionary, anal".
        if (modifiers.Remove("anal")) acts[0] = "anal " + acts[0];
        if (both) modifiers.Insert(0, "with both girls");
        var parts = new List<string> { string.Join(" + ", acts) };
        parts.AddRange(modifiers);
        if (location) parts.AddRange(places);
        return string.Join(", ", parts);
    }
}
