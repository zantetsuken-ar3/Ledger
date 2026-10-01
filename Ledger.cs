using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;

namespace Ledger;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.zantetsukenar3.ledger";
    public string Name { get; init; } = "Ledger";
    public string Author { get; init; } = "ZantetsukenAR3";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 1)]
public class LedgerMod(
    ISptLogger<LedgerMod> logger,
    ICloner cloner,
    TradersTable tradersTable,
    LocaleTable localeTable,
    TraderConfig traderConfig,
    RagfairConfig ragfairConfig,
    TimeUtil timeUtil,
    ModHelper modHelper,
    ImageRouter imageRouter,
    TemplateTable templateTable,
    PresetHelper presetHelper,
    SaveServer saveServer) : IOnLoad, IOnUpdate
{
    private const string TemplateTraderId = "54cb50c76803fa8b248b4571";
    private const string LedgerTraderId = "68d85e2a7c4f1b03a9e61001";
    private const string VanillaQuestTemplateId = "5936d90786f7742b1420ba5b";
    private const string RoubleTemplateId = "5449016a4bdc2d6f028b456f";
    private const string DogtagCaseTemplateId = "5c093e3486f77430cb02e593";
    private const string DogtagCaseAssortItemId = "68d85e2a7c4f1b03a9e63001";

    private static readonly List<string> AllDogtags =
    [
        "59f32c3b86f77472a31742f0","6662ea05f6259762c56f3189","6662e9f37fa79a6d83730fa0",
        "6764207f2fa5e32733055c4a","6764202ae307804338014c1a","68418091b5b0c9e4c60f0e7a","68f15e53103c5d9d4f022c78","68fb4157b280c103230e3b3c",
        "59f32bb586f774757e1e8442","6662e9cda7e0b43baa3d5f76","6662e9aca7e0b43baa3d5f74",
        "684181208d035f60230f63f9","684180bc51bf8645f7067bc8","675dcb0545b1a2d108011b2b",
        "675dc9d37ae1a8792107ca96","68f15cf222c8979ee308f495","68fb41120760c7891606613c"
    ];
    private static readonly List<string> BearDogtags =
    [
        "59f32bb586f774757e1e8442","6662e9cda7e0b43baa3d5f76","6662e9aca7e0b43baa3d5f74",
        "684181208d035f60230f63f9","684180bc51bf8645f7067bc8","675dcb0545b1a2d108011b2b",
        "675dc9d37ae1a8792107ca96","68f15cf222c8979ee308f495","68fb41120760c7891606613c"
    ];
    private static readonly List<string> UsecDogtags =
    [
        "59f32c3b86f77472a31742f0","6662ea05f6259762c56f3189","6662e9f37fa79a6d83730fa0",
        "6764207f2fa5e32733055c4a","6764202ae307804338014c1a","68418091b5b0c9e4c60f0e7a","68f15e53103c5d9d4f022c78","68fb4157b280c103230e3b3c"
    ];

    private readonly Dictionary<string, string> _resolvedItems = new(StringComparer.OrdinalIgnoreCase);
    private int _activeRewardQuest;

    // Stable vanilla item IDs for names that differ between SPT locale releases.
    private static readonly Dictionary<string, string> ExactItemIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bolts"] = "57347c5b245977448d35f6e1",
        ["factorykey"] = "5448ba0b4bdc2d02308b456c",
        ["rbvo"] = "5d80c62a86f7744036212b3f",
        ["rbst"] = "5d9f1fa686f774726974a992",
        ["rbak"] = "5d80c78786f774403a401e3e"
    };

    // These rewards are naturally represented as stacks. Other rewards are emitted as
    // separate item instances so non-stackable equipment/keycards never receive invalid counts.
    private static readonly HashSet<string> StackableRewardKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "m855a1", "50bmg", "m80", "pbp", "545bp", "762ps", "m61", "76254high",
        "m62", "76239high", "545bt", "m855", "545bs", "338fmj", "magnum", "ap20"
    };

    private static readonly Dictionary<string, string[]> ItemAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["beer"] = ["Bottle of Pevko Light beer", "Pevko Light beer"],
        ["tea"] = ["42 Signature Blend English Tea", "English Tea"],
        ["alyonka"] = ["Alyonka chocolate bar", "Alyonka"],
        ["vodka"] = ["Bottle of Tarkovskaya vodka", "Tarkovskaya vodka"],
        ["m855a1"] = ["5.56x45mm M855A1"],
        ["crackers"] = ["Army Crackers", "Army crackers"],
        ["condensedmilk"] = ["Can of condensed milk", "Condensed milk"],
        ["tushonka"] = ["Can of beef stew (Large)", "Can of beef stew (Small)", "Tushonka"],
        ["toiletpaper"] = ["Toilet paper"],
        ["50bmg"] = ["12/70 makeshift .50 BMG slug", "12/70 .50 BMG slug"],
        ["m80"] = ["7.62x51mm M80"],
        ["water"] = ["Bottle of water (0.6L)", "Water bottle"],
        ["lightbulb"] = ["Light bulb"],
        ["pbp"] = ["9x19mm PBP gzh", "9x19mm PBP"],
        ["screwdriver"] = ["Screwdriver"],
        ["sugar"] = ["Pack of sugar", "Sugar"],
        ["pliers"] = ["Pliers"],
        ["measuringtape"] = ["Construction measuring tape", "Measuring tape"],
        ["545bp"] = ["5.45x39mm BP gs", "5.45x39mm BP"],
        ["weaponparts"] = ["Weapon parts"],
        ["bolts"] = ["Bolts"],
        ["screwnuts"] = ["Screw nuts"],
        ["762ps"] = ["7.62x39mm PS gzh", "7.62x39mm PS"],
        ["ducttape"] = ["Duct tape"],
        ["insulatingtape"] = ["Insulating tape"],
        ["peas"] = ["Can of green peas", "Green peas"],
        ["coffee"] = ["Can of Dr. Lupo's coffee beans", "Coffee beans"],
        ["holo"] = ["EOTech XPS3-0 holographic sight", "EOTech 553 holographic sight"],
        ["laser"] = ["Steiner DBAL-PL tactical device", "AN/PEQ-15 tactical device"],
        ["foregrip"] = ["Zenit RK-2 tactical foregrip", "Magpul RVG foregrip"],
        ["stock"] = ["AR-15 Magpul CTR Carbine stock", "Magpul CTR Carbine stock", "Magpul CTR stock"],
        ["suppressor"] = ["SilencerCo Saker ASR 556 5.56x45 sound suppressor", "SilencerCo Hybrid 46 sound suppressor"],
        ["grizzly"] = ["Grizzly medical kit"],
        ["propital"] = ["Propital regenerative stimulant injector", "Propital"],
        ["salewa"] = ["Salewa first aid kit"],
        ["gunoil"] = ["Gunpowder \"Eagle\"", "Weapon parts", "Gun lubricant"],
        ["rsass"] = ["Remington R11 RSASS 7.62x51 marksman rifle", "R11 RSASS"],
        ["x25"] = ["X Products X-25 7.62x51 50-round drum magazine", "X-25 50-round drum magazine"],
        ["m61"] = ["7.62x51mm M61"],
        ["76254high"] = ["7.62x54mm R BS gs", "7.62x54mm R SNB gzh", "7.62x54mm R PS gzh"],
        ["class6armor"] = ["NFM THOR Integrated Carrier body armor", "LBT-6094A Slick Plate Carrier"],
        ["helmet"] = ["Galvion Caiman Hybrid helmet", "Ops-Core FAST MT Super High Cut helmet", "HighCom Striker ULACH IIIA helmet"],
        ["afak"] = ["AFAK tactical individual first aid kit", "AFAK"],
        ["osprey"] = ["Ars Arma CPC MOD.1 plate carrier", "Crye Precision AVS plate carrier", "WARTECH TV-110 plate carrier"],
        ["pmag60"] = ["AR-15 5.56x45 Magpul PMAG D-60 STANAG 60-round magazine", "Magpul PMAG D-60 5.56x45 60-round magazine", "PMAG D-60"],
        ["headset"] = ["Peltor ComTac VI headset", "Peltor ComTac V headset", "MSA Sordin Supreme PRO-X/L active headset"],
        ["grenade"] = ["F-1 hand grenade", "RGD-5 hand grenade"],
        ["zagustin"] = ["Zagustin hemostatic drug injector", "Zagustin"],
        ["etgc"] = ["eTG-change regenerative stimulant injector", "eTG-c"],
        ["bitcoin"] = ["Physical Bitcoin"],
        ["bandage"] = ["Army bandage", "Aseptic bandage"],
        ["surv12"] = ["Surv12 field surgical kit", "Surv12"],
        ["m62"] = ["7.62x51mm M62 Tracer", "7.62x51mm M62"],
        ["aquamari"] = ["Water bottle with a filter Aquamari", "Aquamari"],
        ["76239high"] = ["7.62x39mm BP gzh", "7.62x39mm PP gzh", "7.62x39mm MAI AP"],
        ["glock17"] = ["GLOCK 17 Gen3 9x19 pistol", "GLOCK 17 9x19 pistol"],
        ["glockmag"] = ["Glock 9x19 \"Big Stick\" 33-round magazine", "Glock 9x19 33-round magazine"],
        ["m4a1"] = ["Colt M4A1 5.56x45 assault rifle", "M4A1"],
        ["reapir"] = ["Trijicon REAP-IR thermal scope", "REAP-IR"],
        ["m1a"] = ["Springfield Armory M1A 7.62x51 rifle", "M1A"],
        ["aks74u"] = ["Kalashnikov AKS-74U 5.45x39 assault rifle", "AKS-74U"],
        ["kedr"] = ["PP-91 Kedr 9x18PM submachine gun", "PP-91 Kedr"],
        ["mp5"] = ["HK MP5 9x19 submachine gun"],
        ["mp133"] = ["MP-133 12ga pump-action shotgun", "MP-133"],
        ["toz"] = ["TOZ-106 20ga bolt-action shotgun", "TOZ-106"],
        ["vpo"] = ["Molot VPO-136 Vepr-KM 7.62x39 carbine", "VPO-136"],
        ["optic"] = ["Vortex Razor HD Gen.2 1-6x24 30mm riflescope", "Valday PS-320 1/6x scope"],
        ["gpu"] = ["Graphics card"],
        ["vpx"] = ["VPX Flash Storage Module", "VPX"],
        ["virtex"] = ["Virtex programmable processor", "Virtex"],
        ["milcable"] = ["Military cable"],
        ["mpfilter"] = ["Military power filter"],
        ["labscard"] = ["TerraGroup Labs access keycard", "Labs access keycard"],
        ["ssd"] = ["SSD drive", "SSD"],
        ["wires"] = ["Wires"],
        ["capacitor"] = ["Capacitors", "Capacitor"],
        ["circuitboard"] = ["Printed circuit board", "Circuit board"],
        ["psu"] = ["PC CPU", "Power supply unit", "PSU"],
        ["food"] = ["Can of sprats", "Can of green peas", "Iskra ration pack"],
        ["paper"] = ["Printer paper"],
        ["flashdrive"] = ["Secure Flash drive", "Secure Flash Drive"],
        ["analgin"] = ["Analgin painkillers", "Analgin"],
        ["ibuprofen"] = ["Ibuprofen painkillers", "Ibuprofen"],
        ["goldenstar"] = ["Golden Star balm", "Golden Star"],
        ["sunglasses"] = ["RayBench Hipster Reserve sunglasses", "Dundukk sport sunglasses"],
        ["cigarettes"] = ["Malboro Cigarettes", "Wilston cigarettes", "Strike Cigarettes"],
        ["dorm314"] = ["Dorm room 314 marked key", "Dorm room 314 key"],
        ["factorykey"] = ["Factory emergency exit key"],
        ["rbvo"] = ["RB-VO marked key"],
        ["rbst"] = ["RB-ST key"],
        ["rbak"] = ["RB-AK key"],
        ["thor"] = ["NFM THOR Integrated Carrier body armor"],
        ["slick"] = ["LBT-6094A Slick Plate Carrier", "LBT-6094A Slick plate carrier"],
        ["ulach"] = ["HighCom Striker ULACH IIIA helmet (Black)"],
        ["fastmt"] = ["Ops-Core FAST MT Super High Cut helmet (Black)"],
        ["caiman"] = ["Galvion Caiman Hybrid helmet", "Galvion Caiman Hybrid helmet (Black)"],
        ["goldentt"] = ["Golden TT", "TT-33 7.62x25 TT pistol (Golden)"],
        ["killahelmet"] = ["Maska-1SCh bulletproof helmet (Killa)", "Maska-1SCh", "Killa helmet", "Killa"],
        ["545bt"] = ["5.45x39mm BT gs", "5.45x39mm BT"],
        ["energy"] = ["Hot Rod energy drink", "Hot Rod"],
        ["maxenergy"] = ["Max Energy energy drink", "Max Energy"],
        ["bearbuddy"] = ["BEAR Buddy plush toy", "BEAR Buddy"],
        ["g28"] = ["HK G28 7.62x51 marksman rifle", "G28"],
        ["spear"] = ["SIG MCX-SPEAR 6.8x51 assault rifle", "MCX-SPEAR"],
        ["mk18"] = ["SWORD International Mk-18 .338 LM marksman rifle", "Mk-18 .338 LM marksman rifle"],
        ["thiccweaponcase"] = ["T H I C C Weapon case", "T H I C C weapon case"],
        ["calok"] = ["CALOK-B hemostatic applicator", "CALOK-B"],
        ["iskra"] = ["Iskra ration pack"],
        ["cms"] = ["CMS surgical kit", "CMS"],
        ["ifak"] = ["IFAK individual first aid kit", "IFAK"],
        ["ammocase"] = ["Ammunition case", "Ammo case"],
        ["magcase"] = ["Magazine case"],
        ["medcase"] = ["Medicine case", "Medcase"],
        ["m855"] = ["5.56x45mm M855"],
        ["545bs"] = ["5.45x39mm BS gs", "5.45x39mm BS"],
        ["338fmj"] = [".338 Lapua Magnum FMJ"],
        ["labsviolet"] = ["TerraGroup Labs keycard (Violet)", "TerraGroup Labs keycard (Violet)"],
        ["labsgreen"] = ["TerraGroup Labs keycard (Green)", "TerraGroup Labs keycard (Green)"],
        ["labsred"] = ["TerraGroup Labs keycard (Red)", "TerraGroup Labs keycard (Red)"],
        ["magnum"] = ["12/70 8.5mm Magnum buckshot", "12/70 Magnum buckshot"],
        ["ap20"] = ["12/70 AP-20 armor-piercing slug", "12/70 AP-20"],
        ["alusplint"] = ["Aluminum splint", "Aluminium splint"],
        ["suppressor_saker"] = ["SilencerCo Saker ASR 556 5.56x45 sound suppressor"],
        ["suppressor_hybrid"] = ["SilencerCo Hybrid 46 multi-caliber sound suppressor", "SilencerCo Hybrid 46 sound suppressor"],
        ["suppressor_nt4"] = ["KAC QDSS NT4 5.56x45 sound suppressor (Black)", "KAC QDSS NT4 5.56x45 sound suppressor (FDE)", "KAC QDSS NT4 5.56x45 sound suppressor", "KAC QDSS NT-4 5.56x45 sound suppressor"],
        ["suppressor_sdn6"] = ["AAC 762-SDN-6 multi-caliber sound suppressor", "AAC 762-SDN-6 sound suppressor"],
        ["suppressor_osprey9"] = ["SilencerCo Osprey 9 9x19 sound suppressor", "SilencerCo Osprey 9 sound suppressor"],
        ["laser_dbal"] = ["Steiner DBAL-PL tactical device"],
        ["laser_peq15"] = ["AN/PEQ-15 tactical device"],
        ["laser_x400"] = ["SureFire X400 Ultra tactical flashlight with laser", "SureFire X400 Ultra"],
        ["laser_klesch"] = ["Zenit Klesch-2IKS IR illuminator with laser", "Klesch-2IKS"],
        ["grip_rk2"] = ["Zenit RK-2 tactical foregrip"],
        ["grip_rvg"] = ["Magpul RVG foregrip"],
        ["grip_se5"] = ["Stark SE-5 Express Forward foregrip", "SE-5 Express Forward"],
        ["grip_rk1"] = ["Zenit RK-1 tactical foregrip"],
        ["optic_razor"] = ["Vortex Razor HD Gen.2 1-6x24 30mm riflescope"],
        ["optic_valday"] = ["Valday PS-320 1/6x scope"],
        ["optic_vudu"] = ["EOTech Vudu 1-6x24 30mm riflescope"],
        ["optic_specter"] = ["ELCAN SpecterDR 1x/4x scope"],
        ["cofdm"] = ["Military COFDM Wireless Signal Transmitter"],
        ["rfid"] = ["UHF RFID Reader"],
        ["mcb"] = ["Military circuit board"],
        ["phasearray"] = ["Phased array element"],
        ["iridium"] = ["Iridium military thermal vision module"],
        ["rbbk"] = ["RB-BK marked key"],
        ["rbpkpm"] = ["RB-PKPM marked key"],
        ["chek15"] = ["Chekannaya 15 apartment key", "Chek 15 apartment key"],
        ["abandonedmarked"] = ["Abandoned factory marked key"],
        ["itemcase"] = ["Items case", "Item case"],
        ["docscase"] = ["Documents case", "Document case"],
        ["weaponcase"] = ["Weapon case"],
        ["sicccase"] = ["S I C C organizational pouch", "S I C C case"],
        ["thiccitemcase"] = ["T H I C C Items case", "T H I C C item case"],
        ["dogtagcase"] = ["Dogtag case"],
    };

    private sealed record ObjectiveSpec(string Kind, string Key, int Count, int Level);
    private sealed record QuestSpec(int Number, string Title, int Xp, int Cash, double Rep, int Image,
        string Intro, string Success, List<ObjectiveSpec> Objectives);

    private static readonly List<QuestSpec> QuestSpecs =
    [        new QuestSpec(1, "First Entry", 2500, 15000, 0.1, 1,
            "You're new. Or new enough. I don't need your rifle or ammunition; I collect information. Proper information. Bring me one PMC tag. BEAR, USEC, I don't care. Read the name before you hand it over. People skip that part. I don't know why. And please, don't clean it.",
            "Ah. There we are. Scratches, dirt, wear. History. No, I wasn't smelling it. Take the beer; I don't drink. Come back later.",
            [new ObjectiveSpec("dog", "all", 1, 0)]),        new QuestSpec(2, "See Both Sides", 3500, 25000, 0.1, 1,
            "One specimen tells you very little. I need comparison: one BEAR and one USEC. Differences in issue, manufacture, wear. Names too. I had a Krupa in one pile and another Krupa in a different set of notes. Probably unrelated. Probably. The details matter.",
            "Good. One of each. That's all the alcohol I had, by the way. Tea and chocolate are much more civilised.",
            [new ObjectiveSpec("dog", "bear", 1, 0), new ObjectiveSpec("dog", "usec", 1, 0)]),        new QuestSpec(3, "A Little Experience", 5000, 35000, 0.1, 1,
            "Fresh tags tell me very little. Bring me three from operators level twenty-five or higher. I want people who lasted long enough to learn something. There is a USEC called Lukáš \"Forester\" Krupa in one of my notes. I keep wondering whether the nickname describes a habit, a place, or merely somebody else's joke. That is the trouble with names. They tell you more and less than numbers at the same time.",
            "Much better. The edges have history. People record experience with numbers; the metal keeps another record. Take the vodka and ammunition. I found them. They're no use to me.",
            [new ObjectiveSpec("dog", "all", 3, 25)]),        new QuestSpec(4, "Mind the Gap", 6000, 50000, 0.1, 1,
            "I've reorganised the collection and found an irritating gap. Five tags should correct it. Any faction, any level. This one is about filling the space. Although I found myself remembering a man called Miron \"Skala\" Timofeev while I was moving the trays. I don't remember his number. That seems backwards.",
            "Five. Good. That fits rather nicely. Take the crackers and condensed milk. I bought too much. No, I don't know why.",
            [new ObjectiveSpec("dog", "all", 5, 0)]),        new QuestSpec(5, "Practical Considerations", 7500, 60000, 0.1, 1,
            "Four BEAR and two USEC. No, the ratio isn't arbitrary. I have a reason. You don't need the reason. There are surnames that appear on both sides, incidentally. Petrov. Vardanyan. Gasparyan. It complicates things. Faction is supposed to simplify the record, not make me wonder whether two dead men were cousins.",
            "Four and two. Correct. Your payment is over there: meat, toilet paper and shotgun shells. They're not related.",
            [new ObjectiveSpec("dog", "bear", 4, 0), new ObjectiveSpec("dog", "usec", 2, 0)]),        new QuestSpec(6, "A Particular Standard", 8500, 70000, 0.1, 1,
            "Four tags, level seventeen or higher. Seventeen is the appropriate threshold. I've done the calculations. No, you can't see them. A man called Daniil \"Rebus\" Belov survived long enough to appear in three separate scraps of information I was given. Or there were three Belovs. I need better records.",
            "Four. Seventeen or above. Good. Seventeen rounds of M80 too. Yes, also seventeen. Take the water and bulbs while you're at it.",
            [new ObjectiveSpec("dog", "all", 4, 17)]),        new QuestSpec(7, "Return to Sender", 10000, 85000, 0.1, 1,
            "I realised I made a mistake when we first met. I gave you a bottle. I meant to give you the beer. Bring me a bottle of beer back, and seven tags while you're at it. I'll dispose of the contents. Also, if you see the surname Leone, don't discard it. I have several Leones in the notes now. That may mean something, or Italians simply have surnames.",
            "Yes, that's the bottle. Probably. They all look remarkably similar. Take the ammunition and the contents of that drawer. None of it is related.",
            [new ObjectiveSpec("dog", "all", 7, 0), new ObjectiveSpec("item", "beer", 1, 0)]),        new QuestSpec(8, "Underfoot", 11000, 95000, 0.1, 1,
            "I've got another gap. Nine should do it. Any kind. Read a few of the names before you bring them. I found one listed as John \"Tag\" Price, which is either an extraordinary coincidence or proof that mercenaries have a worse sense of humour than I thought. Also remind me about the ammunition under the desk. I've stubbed my toe on the box three times.",
            "Nine. Good. Take the box, and those parts beside it. If I injure myself on any of them again I'm holding you responsible.",
            [new ObjectiveSpec("dog", "all", 9, 0)]),        new QuestSpec(9, "Bulk Purchase", 12500, 125000, 0.1, 1,
            "I've been approaching this inefficiently. This time bring me fifteen tags, any fifteen. I want volume. No, this doesn't mean I've stopped caring about the details. Quite the opposite. With enough names, the repetitions start to bother you. Families, common surnames, nicknames reused by strangers. Or perhaps I am inventing patterns because blank columns annoy me.",
            "Fifteen. Reassuring quantity. Unfortunately they all need sorting now. Take the ammunition, tape, and the peas. They're peas.",
            [new ObjectiveSpec("dog", "all", 15, 0)]),        new QuestSpec(10, "Many Happy Returns", 15000, 150000, 0.1, 1,
            "Fifteen at once was a mistake. I've been awake most of the night cataloguing them. Bring me proper coffee and five tags level twenty or higher. Also, I think it's my birthday. Approximately. The flea contact who calls himself Night Clerk says birthdays matter. I don't know why I take social advice from a man named Night Clerk.",
            "Coffee. Five tags. Birthday. Good enough. Apparently people exchange gifts on birthdays, so take those attachments. Technically I think you're supposed to give them to me, but that seems inefficient.",
            [new ObjectiveSpec("dog", "all", 5, 20), new ObjectiveSpec("item", "coffee", 1, 0)]),        new QuestSpec(11, "Preventative Measures", 17500, 175000, 0.1, 2,
            "I want symmetry: five BEAR, five USEC, all level eighteen or higher. Ten total. Nice and balanced. No, eighteen isn't arbitrary. Arkady \"Tigr\" Rodionov is in my notes twice. I think. One entry says Customs, another says Woods. If it is the same man, he moved around. If it isn't, I have wasted twenty minutes comparing handwriting.",
            "Five and five. Excellent. Take the medical gear and the suppressor. You do get shot at rather a lot. That's an observation, not concern.",
            [new ObjectiveSpec("dog", "bear", 5, 18), new ObjectiveSpec("dog", "usec", 5, 18)]),        new QuestSpec(12, "An Uneven Sample", 18500, 190000, 0.1, 2,
            "I've made another mistake. Five and five looks excellent, which apparently makes it statistically unhelpful. I need three BEAR and eight USEC. Uneven samples show different things. So do uneven lives, apparently. One of the names I have is Finlay \"Wrongway\" Baker. I keep wondering whether the nickname was earned before or after Tarkov.",
            "Three. Eight. Eleven. Less attractive, more useful. I dislike statistics. Take the ammunition and gunpowder; they're your problem now.",
            [new ObjectiveSpec("dog", "bear", 3, 0), new ObjectiveSpec("dog", "usec", 8, 0)]),        new QuestSpec(13, "Disaster", 20000, 225000, 0.1, 2,
            "Disaster. Absolute disaster. I could have sworn there were five here. I need five USEC tags, level twenty-three or higher, as soon as reasonably possible. I have a gap. I also have two entries for men called Leone and one for a \"Lion\" Leone. I am beginning to resent surnames.",
            "Yes. There. Much better. I don't know how I was supposed to work with that staring at me all day. Take that rifle; someone gave it to me as payment. Apparently it's rather good.",
            [new ObjectiveSpec("dog", "usec", 5, 23)]),        new QuestSpec(14, "Life Expectancy", 22500, 275000, 0.1, 2,
            "Level thirty interests me. By then somebody must have learned something. Bring me ten tags, level thirty or higher. There must be a pattern to survival. Route choice, equipment, patience, aggression, luck. I have a man called Michael \"Backtrack\" Scott in the notes. Perhaps he understood something useful. Or perhaps he simply got lost often enough to acquire a nickname.",
            "Ten experienced operators. Different factions, different wear, no obvious pattern. They're all dead, yes; I had noticed. That's rather the problem. Take the armour, medicine and ammunition. Perhaps they'll be useful.",
            [new ObjectiveSpec("dog", "all", 10, 30)]),        new QuestSpec(15, "Another Variable", 24000, 300000, 0.1, 2,
            "The last sample was inconclusive. Perhaps faction is the missing variable. Six BEAR, six USEC, all level twenty-four or higher. Same quantity, different training. I have USEC and BEAR records sharing surnames now. Vardanyan. Tsereteli. Mirzoyan. I cannot tell whether that means family, coincidence, or that I am asking the wrong question.",
            "Six and six. Nothing useful. Do you people deliberately make yourselves statistically inconvenient? I've put equipment aside for you. Use it properly. Reliable suppliers are difficult to replace.",
            [new ObjectiveSpec("dog", "bear", 6, 24), new ObjectiveSpec("dog", "usec", 6, 24)]),        new QuestSpec(16, "Taking This Seriously", 30000, 350000, 0.1, 2,
            "I've noticed something. You're keeping these, aren't you? You're trying to anticipate what I'll ask for before I ask for it. That means you're not looking at them properly when you take them. You're just storing inventory. That isn't what we're doing. I thought this was a study, not a courier service. Bring me forty tags, any faction, any level. Consider it an audit. And this time, read the names. Forty is excessive. That's precisely the point.",
            "Forty. You actually had forty. That's slightly concerning. I've taken the liberty of helping: here's a new case. It's empty. Try to use it responsibly.",
            [new ObjectiveSpec("dog", "all", 40, 0)]),        new QuestSpec(17, "No Hard Feelings", 20000, 750000, 0.1, 2,
            "All right. Forty may have been excessive. I was making a point. Possibly too firmly. Three this time, level twenty-one or above. Why twenty-one? Because twenty wouldn't be enough. They're different requirements. I found a note about a trader calling himself Bookkeeper. I dislike the name. It feels competitive.",
            "Three. Good. I recognise the previous assignment may have been... inconvenient. This isn't compensation. Stop smiling.",
            [new ObjectiveSpec("dog", "all", 3, 21)]),        new QuestSpec(18, "Against the Odds", 25000, 400000, 0.1, 2,
            "Suppose one USEC is alone and four BEAR are closing in. He can't leave; the route behind him is blocked. Bring me one USEC and four BEAR, level fifteen or higher. I want to arrange something. Call the USEC Gabriel \"Hollywood\" Williams for the moment. A ridiculous name, but a useful marker. If Hollywood survives, I want to know why.",
            "There. Four here, one there. Cover, ammunition, a defensible position... yes, he might make it. Take the rig and magazines. Apparently sixty rounds is useful when four people are trying to kill you.",
            [new ObjectiveSpec("dog", "usec", 1, 15), new ObjectiveSpec("dog", "bear", 4, 15)]),        new QuestSpec(19, "Reinforcements", 27500, 450000, 0.1, 2,
            "I continued the scenario. Hollywood survived. Now six USEC have arrived, and two more BEAR. Bring me six USEC and two BEAR, all level twenty-eight or higher. Please don't mix them together. I know none of these are actually Hollywood. That isn't the point. Or perhaps it is. I'm still deciding.",
            "Good. If he'd had a decent headset, like one of these, communications would have been better. The injectors would have helped too. Take them. Actually, I don't need any. Take all of them.",
            [new ObjectiveSpec("dog", "usec", 6, 28), new ObjectiveSpec("dog", "bear", 2, 28)]),        new QuestSpec(20, "Poor Planning", 30000, 500000, 0.1, 2,
            "There's a problem. Seven BEAR are advancing on three USEC. Their position is defensible, but I've calculated the ammunition expenditure. They didn't bring enough. Level thirty-two or higher. There is a BEAR called Nikita \"Strelok\" Sukhanov in the records. Strelok means shooter. Useful nickname. Useless information unless I know whether he survived because he could shoot or was called that because he could shoot.",
            "There. He's out of ammunition forty-three seconds before the others. Completely avoidable. If he'd carried three magazines like these he'd still be fighting. Carry enough. And take the bandage.",
            [new ObjectiveSpec("dog", "usec", 3, 32), new ObjectiveSpec("dog", "bear", 7, 32)]),        new QuestSpec(21, "Contingency", 32500, 600000, 0.1, 3,
            "I've changed the scenario. Four BEAR, four USEC, level twenty-eight or higher. Twenty-eight gives the most useful result. Assume the first plan failed. Now account for bleeding, exhaustion, bad information and panic. People keep treating plans as if failure is an exception. I am beginning to think failure is simply another column.",
            "Four and four. If he moves here, treats the bleeding immediately... yes. He gets out. Not comfortably, but he gets out. Take the armour, medicine and ammunition. I've accounted for the most likely failures.",
            [new ObjectiveSpec("dog", "bear", 4, 28), new ObjectiveSpec("dog", "usec", 4, 28)]),        new QuestSpec(22, "Back to Basics", 35000, 650000, 0.1, 3,
            "I've stopped the scenarios for the moment. They weren't producing anything useful. Change enough variables and you can construct any outcome you want. Seven BEAR, nine USEC. No minimum level. Just facts. A contact called Quartermaster says this is how supply works too. I suspect he is trying to sell me something.",
            "Seven. Nine. Sixteen. Good. Facts are easier; they don't change because you'd prefer a different answer. Take the card and water. Hydration is important. That's general advice.",
            [new ObjectiveSpec("dog", "bear", 7, 0), new ObjectiveSpec("dog", "usec", 9, 0)]),        new QuestSpec(23, "Statistical Correction", 37500, 700000, 0.1, 3,
            "There's an error. Six point four percent. Small, but still an error. Six tags, level thirty-three or above. That should correct it. I saw the name Yaroslav \"Volk\" Konovalov again. Or I think I did. If I start recognising people who are supposed to be random samples, the sample is no longer random. Annoying.",
            "Six. There. Corrected. I prefer problems with solutions. Your payment is over there: sixty-six rounds. Yes, sixty-six. That's the correct quantity. Don't start.",
            [new ObjectiveSpec("dog", "all", 6, 33)]),        new QuestSpec(24, "I Don't See the Appeal", 40000, 750000, 0.1, 3,
            "I want to understand guns. Not what they do. The fascination. Bring me a standard Glock 17 and three tags, level twenty or higher. A man in the flea notes calls himself Gunsmith, which is either reassuringly literal or deeply suspicious. Do not customise the Glock. I want the boring version.",
            "I still don't see the appeal. Although the sight was crude, the controls untidy, the magazine inadequate... I made some adjustments. Take this one instead. I don't need two.",
            [new ObjectiveSpec("dog", "all", 3, 20), new ObjectiveSpec("item", "glock17", 1, 0)]),        new QuestSpec(25, "Second Opinion", 45000, 850000, 0.1, 3,
            "I've been experimenting. Not because I'm interested; the Glock simply wasn't representative. Bring me five tags, level nineteen or higher. Then I want your opinion on two rifles. I keep seeing equipment described as if it explains the person carrying it. It doesn't. But perhaps it explains what they expected to happen.",
            "Good. Five. Now, these. One lets you see heat, which seems extraordinarily unfair, so naturally I included it. The other is for longer distances. Try them. Everything I know about firearms is relatively recent.",
            [new ObjectiveSpec("dog", "all", 5, 19)]),        new QuestSpec(26, "Surplus to Requirements", 47500, 900000, 0.1, 3,
            "I've learned enough about firearms to identify a problem: people have been giving me rubbish. Bring me eight tags, level fifteen or higher. I'm clearing the surplus at the same time. If one of the names happens to be Mitchell \"Tiny\" Hudson, tell me. No reason. I just want to know whether Tiny was, in fact, tiny. This work is becoming less dignified.",
            "Eight. Your payment is there. Yes, all of them. Sell them, strip them, throw them at somebody. Last time I gave you two extremely good ones. It averages out.",
            [new ObjectiveSpec("dog", "all", 8, 15)]),        new QuestSpec(27, "Enough of That", 50000, 1000000, 0.1, 3,
            "I've stopped studying firearms. Yesterday I spent forty-three minutes comparing two muzzle devices. That's not research, it's an illness. Bring me three BEAR and five USEC, level twenty-two or higher. I'm emptying the attachment drawer. Every piece is different this time. I checked. Twice.",
            "Eight tags. Good. No rails, no mounting standards, no arguments about barrel length. Take all the gun parts too; I'm clearing the drawer before I'm tempted to build something else.",
            [new ObjectiveSpec("dog", "bear", 3, 22), new ObjectiveSpec("dog", "usec", 5, 22)]),        new QuestSpec(28, "Obsolescence", 52500, 1000000, 0.1, 3,
            "Somebody laughed at my computer. It starts, displays information and stores my records; I considered that sufficient. Apparently it isn't. Bring me one graphics card and five tags level twenty-six or higher. There is a USEC called Dev \"Sparky\" Akhtar in my notes. If he understood computers, I wish he'd left instructions instead of a nickname.",
            "That's considerably larger than I expected. Where does it go? Never mind. How complicated can replacing one component possibly be? Take the spare electronics. I may have labelled some incorrectly.",
            [new ObjectiveSpec("dog", "all", 5, 26), new ObjectiveSpec("item", "gpu", 1, 0)]),        new QuestSpec(29, "Requirements", 55000, 1100000, 0.1, 3,
            "The graphics card has created a problem. Power, cabling, cooling, compatibility. Every improvement generates three more requirements. Bring me a military cable, a military power filter and six tags level twenty-eight or higher. I have begun to appreciate why the dead are easier to catalogue than computers.",
            "Neither of these fits. Of course they don't. Apparently I've misunderstood something fundamental. Take the electronics; they were in the same box and I'm beginning to resent the entire category. Technology is stupid.",
            [new ObjectiveSpec("dog", "all", 6, 28), new ObjectiveSpec("item", "milcable", 1, 0), new ObjectiveSpec("item", "mpfilter", 1, 0)]),        new QuestSpec(30, "Analogue", 60000, 1250000, 0.1, 3,
            "I've dismantled the computer. All of it. I'm done. Bring me nine tags. Any kind. Metal, stamped numbers, no drivers, no firmware. A flea trader called Red Ledger offered to buy the old components. I declined on principle. There should not be two Ledgers in one market.",
            "Nine. Perfect. Drop one on the floor? Still works. Get it wet? Still works. No updates, no passwords, no fucking cables. ...Excuse me. That was unnecessary. I've had a difficult evening. Take what is left of the computer.",
            [new ObjectiveSpec("dog", "all", 9, 0)]),        new QuestSpec(31, "What Remains", 45000, 750000, 0.1, 4,
            "I've been wondering what these actually represent. A name, number, faction. That's what remains after everything somebody did and knew. Bring me three. I don't think the numbers matter this time. One of the USEC names is Giulio \"Duke\" Leone. I've seen Leone before. Several times, actually. If Duke is still out there, I would like to know what he keeps doing right.",
            "Three people. I wonder whether anyone still expects one of them to come home. I've spent a great deal of time studying how people die; perhaps I've neglected that they lived first. Strange. I don't suppose I've ever considered who would look for me.",
            [new ObjectiveSpec("dog", "all", 3, 0)]),        new QuestSpec(32, "Paper Trail", 50000, 900000, 0.1, 4,
            "There's been a minor complication. My records were on the computer. Paper doesn't crash or require drivers. Bring me printer paper, three secure flash drives, and five tags level twenty-five or higher. Yes, I realise asking for flash drives while complaining about computers is inconsistent. The ledger is allowed one contradiction.",
            "Paper. Excellent. Flash drives... I've just realised I have nothing to plug those into. Never mind. I've started adding notes about the tags now, not just numbers. It makes the records less efficient. I think I prefer them that way.",
            [new ObjectiveSpec("dog", "all", 5, 25), new ObjectiveSpec("item", "paper", 2, 0), new ObjectiveSpec("item", "flashdrive", 3, 0)]),        new QuestSpec(33, "Administrative Burden", 52500, 1000000, 0.1, 4,
            "Paper was a good idea in principle. I've now written hundreds of names. My hand hurts, my neck hurts, my head hurts. Bring me two packs of painkillers and seven tags level twenty-nine or higher. I wrote 'Duke Leone' in the margin and then found Tommaso \"Lion\" Leone elsewhere. Different man. Probably. This is exactly the sort of thing that keeps me awake.",
            "Seven more names. I'm beginning to understand why computers were invented. I'm not rebuilding it. The notes are getting longer now. Questions, mostly. Some of them I will never answer. I no longer mind the empty space.",
            [new ObjectiveSpec("dog", "all", 7, 29), new ObjectiveSpec("item", "analgin", 2, 0)]),        new QuestSpec(34, "Occupational Hazard", 55000, 1100000, 0.1, 4,
            "The light has become unpleasant after several hours of paperwork. Bring me something tinted for my eyes, three packs of painkillers, and six tags level thirty-one or higher. I'm solving a problem, not making a fashion statement. Also, I may have found Duke. No. Wait. Fabrizio Leone. Wrong Leone. Ignore that.",
            "Much better. Although... no. The light probably isn't the problem. Never mind. Take this key. Apparently it opens somewhere interesting. Going outside to investigate a mysterious locked room seems an excellent way to become part of my collection.",
            [new ObjectiveSpec("dog", "all", 6, 31), new ObjectiveSpec("item", "sunglasses", 1, 0), new ObjectiveSpec("item", "analgin", 3, 0)]),        new QuestSpec(35, "Local Customs", 65000, 1250000, 0.1, 4,
            "I've been thinking about the locals. Scavs. They don't carry tags, which is inconsiderate, but they carry other things. Bring me cigarettes, two bottles of vodka, and eight tags level thirty-four or higher. Old Vanya says possessions tell you more than paperwork. I have no idea whether Old Vanya is a person, a business, or three scavs sharing a coat.",
            "Cigarettes. Vodka. A tag tells me who somebody was once they're dead; these tell me something while they're alive. What they drink, smoke, what they thought they'd need. More interesting than I expected. Take the consumables back.",
            [new ObjectiveSpec("dog", "all", 8, 34), new ObjectiveSpec("item", "cigarettes", 3, 0), new ObjectiveSpec("item", "vodka", 2, 0)]),        new QuestSpec(36, "One Man in Particular", 70000, 1500000, 0.1, 4,
            "The problem with ordinary possessions is provenance. Apparently one man carries a gold-plated pistol. Distinctive, impractical, interesting. Bring me Reshala's Golden TT and five tags level thirty-five or higher. A tag tells me what somebody was issued. A ridiculous gold pistol tells me what somebody chose to become.",
            "So this belonged to one particular man. A tag identifies someone because somebody stamped it; this tells me something because he chose it himself. Interesting. Take it back. It isn't mine. Take the rest too; I have more money than uses for it.",
            [new ObjectiveSpec("dog", "all", 5, 35), new ObjectiveSpec("item", "goldentt", 1, 0)]),        new QuestSpec(37, "Signs of Life", 75000, 1750000, 0.1, 4,
            "I think I've been looking at this backwards. A tag becomes interesting after its owner dies. Food, water and cigarettes only make sense because somebody expected to still be alive later. Bring me ten tags level thirty-six or higher and some ordinary supplies. I found Ross \"Doc\" Hunt in the notes. If Doc carried medicine, that tells me something. If he didn't, it tells me something else.",
            "The tag tells us what happened. The food tells us they weren't expecting it to happen. That's a significant difference. Anyway, I've become philosophical about canned goods. Let's not encourage that.",
            [new ObjectiveSpec("dog", "all", 10, 36), new ObjectiveSpec("item", "food", 2, 0), new ObjectiveSpec("item", "water", 2, 0), new ObjectiveSpec("item", "cigarettes", 1, 0)]),        new QuestSpec(38, "We've Discussed This", 85000, 2000000, 0.1, 4,
            "We've discussed this. You're doing it again. Twenty tags, level twenty or above. I'll have them now, please. And before you ask, no, you're not getting another case. Read some of the names while you count. I don't care which ones. Actually, I do care. I simply can't explain why without sounding sentimental, so let's not.",
            "Twenty. Good. You knew perfectly well this might happen again. Your payment is there. No dogtag case. The storage case is different. Completely different. Don't start.",
            [new ObjectiveSpec("dog", "all", 20, 20)]),        new QuestSpec(39, "Just One", 90000, 2250000, 0.1, 4,
            "One. That's all I want. Level forty-one or above. Don't simply take the first in your case. Choose one, read the name, think about how long that person survived, then bring it to me. If it happens to say Giulio \"Duke\" Leone, don't get excited. I may already have had his tag. Or his brother's. Or neither. My notes are embarrassingly unclear on this point.",
            "Yes. And you read the name? Good. After twenty at once, I thought perhaps we'd both forgotten what these actually were. Take those rifles. I made a few improvements. Apparently I occasionally relapse.",
            [new ObjectiveSpec("dog", "all", 1, 41)]),        new QuestSpec(40, "Loose Ends", 100000, 2500000, 0.1, 5,
            "I've been going through everything properly. There are a few loose ends. Five tags, level thirty-seven or higher. Five should be enough. Sooner would be preferable. Night Clerk, Bookkeeper and Red Ledger have all appeared in my peripheral notes now. I dislike that the market has developed a bookkeeping theme without consulting me.",
            "Five. Thank you. That section is complete. I've accumulated rather a lot of money, equipment and keys simply because I didn't need anything. Keeping things merely because you can seems rather pointless. Take these. I'm keeping the collection; I'm not finished with that yet.",
            [new ObjectiveSpec("dog", "all", 5, 37)]),        new QuestSpec(41, "Balance Outstanding", 110000, 3000000, 0.1, 5,
            "Three more, level forty or above. That should settle this particular column. Bring me an energy drink too; I've been unusually tired lately. Don't make anything of it. I found Duke's name again last night. Then this morning I realised I'd been reading Edoardo Leone. I am beginning to understand why certainty is expensive.",
            "Three. Still doesn't balance. I used to believe every discrepancy could be found and corrected. Apparently some things don't. Never mind. Take these. I have no reason to keep them now. Don't. I'm tired, that's all.",
            [new ObjectiveSpec("dog", "all", 3, 40), new ObjectiveSpec("item", "energy", 1, 0)]),        new QuestSpec(42, "Reconciliation", 120000, 3500000, 0.1, 5,
            "One tag, any one. Some water and a Max Energy too. And one of those stuffed BEAR toys. No particular reason. It looks comfortable. That is not a sentence I intend to discuss. I spent years trying to turn names like Duke, Strelok, Forester and all the rest into variables. They stubbornly remain people.",
            "Thank you. There's something I should probably tell you. I don't think I have that long left on earth. I don't know precisely how long. I thought if I could balance it, calculate a way through every scenario, perhaps I could avoid this. Position, equipment, preparation. Enough variables, enough planning, and there would be an answer. There isn't. You can do everything correctly and still run out of time. Please stop looking at me like that. I'm still here.",
            [new ObjectiveSpec("dog", "all", 1, 0), new ObjectiveSpec("item", "water", 1, 0), new ObjectiveSpec("item", "maxenergy", 1, 0), new ObjectiveSpec("item", "bearbuddy", 1, 0)]),        new QuestSpec(43, "For the Record", 130000, 4000000, 0.1, 5,
            "I have one more page to finish before I put the ledger away. Bring me some paper and one tag level forty or above. I've begun sorting what I can leave behind and what ought to stay here. Nothing dramatic. I simply want it done properly. If Duke is alive, good. If he isn't, I hope someone read the name before selling the tag.",
            "I've added you to the records. Not the collection. Obviously. Your name. You're the only person I've dealt with regularly enough that it seemed strange not to have you written down somewhere. I suppose that's an address book. Mine has one entry. Very efficient. The rest of the papers are in order too. There won't be much left for you to sort out.",
            [new ObjectiveSpec("dog", "all", 1, 40), new ObjectiveSpec("item", "paper", 1, 0)]),        new QuestSpec(44, "One for the Road", 150000, 5000000, 10.0, 5,
            "I need two bottles of beer. Yes, beer. Two. One for you and one for me. I've packed away the work, and I'd rather spend the time I have left sitting across from you than checking another column. Don't make a thing of it. I asked the Night Clerk whether this counts as closing the books. He said I should stop asking strangers for accounting metaphors.",
            "I still don't particularly like beer. But I see the appeal; it isn't really about the beer. You brought the first bottle back, and then you kept coming back. I spent most of my life assuming I didn't need people. Rather inconveniently, you proved me wrong. I'm glad I knew you. My affairs are in order, and I intend to rest now. Stay a little longer, if you can.",
            [new ObjectiveSpec("item", "beer", 2, 0)]),        new QuestSpec(45, "Books Balanced", 250000, 0, 0.0, 6,
            "If you're reading this, I am gone. I knew we were close to the end when we shared that beer. The instructions are on the desk, and most of what I owned is yours. I have tried to leave as little ambiguity as possible. There isn't anybody else. I am sorry I could not tell you in person. The ledger still contains unresolved names. Duke Leone is one of them. Leave him unresolved. A person is allowed to be more than the answer to one of my questions.",
            "The collection stays where it is; it was never mine to give away. I've left you an empty case. Look at the names occasionally. Knowing something ends doesn't make the time before it less valuable. Thank you for staying. You made these last days better than I expected them to be.\n\nThe books balance. Finally.\n\nOh, and one last thing. The name's Pavel. Thought you ought to know.",
            []),
    ];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResolveKnownItems();
        DiscoverDogtags();
        RegisterTrader();
        RegisterImages();
        RegisterQuests();
        AddTraderLocales();

        logger.Success("[Ledger] Loaded.");
        return Task.CompletedTask;
    }

    public async Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var finalQuestId = QuestId(45);
        var ledgerId = new MongoId(LedgerTraderId);

        foreach (var (profileId, profile) in saveServer.GetProfiles())
        {
            var pmc = profile.CharacterData.PmcData;
            if (pmc?.Quests is null || pmc.TradersInfo is null) continue;

            var finalComplete = pmc.Quests.Any(q => q.QId.ToString() == finalQuestId && q.Status == QuestStatusEnum.Success);
            if (!finalComplete) continue;
            if (!pmc.TradersInfo.TryGetValue(ledgerId, out var traderInfo) || traderInfo is null) continue;
            if (traderInfo.Unlocked == false && traderInfo.Disabled == true) continue;

            traderInfo.Unlocked = false;
            traderInfo.Disabled = true;
            await saveServer.SaveProfileAsync(profileId);
            logger.Success($"[Ledger] Q45 complete for profile {profileId}; Ledger trader disabled. Message history is retained.");
        }

        return true;
    }

    /// <summary>
    /// Builds Ledger's accepted dogtag lists from SPT's live customisation database.
    /// The hard-coded IDs above remain as a fallback, while every registered
    /// DOG_TAGS customisation contributes its USEC and/or BEAR backing item template.
    /// This means vanilla edition/prestige variants and dogtags added by compatible
    /// mods are accepted automatically when they are present at Ledger's load stage.
    /// </summary>
    private void DiscoverDogtags()
    {
        var dogtagCustomisations = templateTable.Customization?
            .Values
            .Where(item =>
                item.Type == "Item"
                && item.Parent == CustomisationTypeId.DOG_TAGS)
            .ToList() ?? [];

        foreach (var dogtag in dogtagCustomisations)
        {
            AddDiscoveredDogtag(dogtag.Properties?.UsecTemplateId, UsecDogtags);
            AddDiscoveredDogtag(dogtag.Properties?.BearTemplateId, BearDogtags);
        }

        foreach (var id in UsecDogtags)
            if (!AllDogtags.Contains(id, StringComparer.OrdinalIgnoreCase))
                AllDogtags.Add(id);

        foreach (var id in BearDogtags)
            if (!AllDogtags.Contains(id, StringComparer.OrdinalIgnoreCase))
                AllDogtags.Add(id);

        logger.Debug(
            $"[Ledger] Dogtag discovery: {UsecDogtags.Count} USEC, " +
            $"{BearDogtags.Count} BEAR, {AllDogtags.Count} total accepted templates.");
    }

    private static void AddDiscoveredDogtag(MongoId? templateId, List<string> target)
    {
        if (templateId is null)
            return;

        var id = templateId.Value.ToString();
        if (string.IsNullOrWhiteSpace(id))
            return;

        if (!target.Contains(id, StringComparer.OrdinalIgnoreCase))
            target.Add(id);
    }

    private void RegisterTrader()
    {
        if (tradersTable.ContainsKey(LedgerTraderId))
        {
            logger.Warning("[Ledger] Trader already exists; quest registration will continue.");
            return;
        }

        if (!tradersTable.TryGetValue(TemplateTraderId, out var templateTrader) || templateTrader?.Base is null)
            throw new InvalidOperationException("[Ledger] Prapor template trader not found.");

        var ledgerBase = cloner.Clone(templateTrader.Base) ?? throw new InvalidOperationException("[Ledger] Trader clone failed.");
        ledgerBase.Id = LedgerTraderId;
        ledgerBase.Name = "Ledger";
        ledgerBase.Nickname = "Ledger";
        ledgerBase.Location = "Old Industrial Quarter";
        ledgerBase.Avatar = $"/files/trader/avatar/{LedgerTraderId}.png";
        ledgerBase.AvailableInRaid = false;
        ledgerBase.UnlockedByDefault = true;
        if (ledgerBase.Insurance is not null) ledgerBase.Insurance.Availability = false;

        ledgerBase.BuyerUp = true;
        ledgerBase.ItemsBuy = new ItemBuyData { Category = [], IdList = AllDogtags.Select(x => new MongoId(x)).ToHashSet() };
        ledgerBase.ItemsBuyProhibited = new ItemBuyData { Category = [], IdList = [] };

        // Ledger is not a conventional shop. Loyalty is earned only through his quest standing;
        // player level and sales volume never gate his stock. LL2 and LL3 arrive early enough
        // for the specialist shop to matter; Q44 guarantees LL4 for the finale.
        ledgerBase.LoyaltyLevels =
        [
            new TraderLoyaltyLevel { MinLevel = 1, MinSalesSum = 0, MinStanding = 0.0, BuyPriceCoefficient = 40 },
            new TraderLoyaltyLevel { MinLevel = 1, MinSalesSum = 0, MinStanding = 0.6, BuyPriceCoefficient = 40 },
            new TraderLoyaltyLevel { MinLevel = 1, MinSalesSum = 0, MinStanding = 1.5, BuyPriceCoefficient = 40 },
            new TraderLoyaltyLevel { MinLevel = 1, MinSalesSum = 0, MinStanding = 4.6, BuyPriceCoefficient = 40 }
        ];

        var assort = new TraderAssort
        {
            Items = [],
            BarterScheme = new Dictionary<MongoId, List<List<BarterScheme>>>(),
            LoyalLevelItems = new Dictionary<MongoId, int>()
        };

        // LL1: basic survival and ammunition. Useful, deliberately expensive, tightly stocked.
        AddShopItem(assort, "dogtagcase", 1, 350000, 1, 1);
        AddShopItem(assort, "calok", 1, 35000, 1, 2);
        AddShopItem(assort, "analgin", 1, 18000, 1, 2);
        AddShopItem(assort, "water", 1, 24000, 1, 2);
        AddShopItem(assort, "iskra", 1, 32000, 1, 2);
        AddShopItem(assort, "m855", 120, 850, 1, 120);
        AddShopItem(assort, "545bp", 120, 950, 1, 120);
        AddShopItem(assort, "salewa", 1, 42000, 1, 2);
        AddShopItem(assort, "alusplint", 1, 28000, 1, 2);
        AddShopItem(assort, "magnum", 80, 650, 1, 80);

        // LL2: after Q20, specialist medical kit, storage and stronger combat supplies.
        AddShopItem(assort, "ammocase", 1, 650000, 2, 1);
        AddShopItem(assort, "cms", 1, 85000, 2, 1);
        AddShopItem(assort, "ifak", 1, 70000, 2, 2);
        AddShopItem(assort, "propital", 1, 80000, 2, 1);
        AddShopItem(assort, "m855a1", 100, 1600, 2, 100);
        AddShopItem(assort, "m80", 80, 1300, 2, 80);
        AddShopItem(assort, "ap20", 60, 2200, 2, 60);
        AddShopItem(assort, "zagustin", 1, 90000, 2, 1);
        AddShopItem(assort, "ulach", 1, 190000, 2, 1);

        // LL3: after Q30, Ledger's best practical surplus. LL4 exists for the finale, not a supermarket tier.
        AddShopItem(assort, "magcase", 1, 600000, 3, 1);
        AddShopItem(assort, "surv12", 1, 185000, 3, 1);
        AddShopItem(assort, "grizzly", 1, 125000, 3, 1);
        AddShopItem(assort, "m62", 60, 2300, 3, 60);
        AddShopItem(assort, "338fmj", 40, 3800, 3, 40);
        AddShopItem(assort, "etgc", 1, 135000, 3, 1);
        AddShopItem(assort, "medcase", 1, 850000, 3, 1);
        AddShopItem(assort, "caiman", 1, 285000, 3, 1);
        AddShopItem(assort, "slick", 1, 675000, 3, 1);

        tradersTable[LedgerTraderId] = new Trader
        {
            Base = ledgerBase,
            Assort = assort,
            QuestAssort = new() { { "Started", new() }, { "Success", new() }, { "Fail", new() } },
            Dialogue = []
        };

        traderConfig.UpdateTime.Add(new UpdateTime
        {
            TraderId = LedgerTraderId,
            Seconds = new MinMax<int>(timeUtil.GetHoursAsSeconds(1), timeUtil.GetHoursAsSeconds(1))
        });
        ragfairConfig.Traders.TryAdd(LedgerTraderId, true);

        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var portrait = System.IO.Path.Combine(modPath, "assets", "Ledger.png");
        imageRouter.AddRoute(ledgerBase.Avatar.Replace(".png", ""), portrait);
    }

    private void AddShopItem(TraderAssort assort, string logicalKey, int stock, int price, int loyalty, int buyLimit)
    {
        var tpl = ResolveItem(logicalKey, false);
        if (tpl is null)
        {
            logger.Warning($"[Ledger] Shop item '{logicalKey}' could not be resolved; it will be omitted from the assort.");
            return;
        }

        var assortId = new MongoId(StableId($"ledger-shop-{logicalKey}"));

        // SPT 4.1.x does not expose a complete discoverable preset for the Slick here,
        // despite quest rewards being able to produce a fully fitted carrier. Use the exact
        // known-good vanilla Slick structure observed in a live 4.1.6 profile instead.
        if (logicalKey == "slick")
        {
            const string slickSoftFront = "6575e71760703324250610c3";
            const string slickSoftBack = "6575e72660703324250610c7";
            const string slickPlate = "656fa76500d62bcd2e024080";

            var root = new Item
            {
                Id = assortId,
                Template = new MongoId(tpl),
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd
                {
                    UnlimitedCount = false,
                    StackObjectsCount = stock,
                    BuyRestrictionMax = buyLimit,
                    BuyRestrictionCurrent = 0
                }
            };
            assort.Items.Add(root);
            assort.Items.Add(new Item
            {
                Id = new MongoId(StableId("ledger-shop-slick-soft-front")),
                Template = new MongoId(slickSoftFront),
                ParentId = assortId.ToString(),
                SlotId = "Soft_armor_front"
            });
            assort.Items.Add(new Item
            {
                Id = new MongoId(StableId("ledger-shop-slick-soft-back")),
                Template = new MongoId(slickSoftBack),
                ParentId = assortId.ToString(),
                SlotId = "Soft_armor_back"
            });
            assort.Items.Add(new Item
            {
                Id = new MongoId(StableId("ledger-shop-slick-front-plate")),
                Template = new MongoId(slickPlate),
                ParentId = assortId.ToString(),
                SlotId = "Front_plate"
            });
            assort.Items.Add(new Item
            {
                Id = new MongoId(StableId("ledger-shop-slick-back-plate")),
                Template = new MongoId(slickPlate),
                ParentId = assortId.ToString(),
                SlotId = "Back_plate"
            });
        }
        else
        {
            var fittedGear = logicalKey is "ulach" or "caiman" or "thor" or "fastmt" or "osprey" or "class6armor" or "helmet";

            if (fittedGear)
            {
                // For the two fitted helmets actually sold by Ledger, prefer the release-safe
                // complete builds captured from a live SPT 4.1.6 profile. Their root template
                // may intentionally differ from the fuzzy alias result, so the known build itself
                // is authoritative.
                List<Item>? items = logicalKey is "ulach" or "caiman"
                    ? BuildKnownFittedGear(logicalKey, tpl)
                    : null;

                if (items is null)
                {
                    var preset = presetHelper.GetDefaultPreset(new MongoId(tpl));
                    items = preset?.Items.Where(x => x.Template.ToString() == tpl || x.ParentId is not null).ToList();
                    var traderBuild = FindTraderWeaponBuild(tpl);
                    if (traderBuild is not null && traderBuild.Count > (items?.Count ?? 0)) items = traderBuild;
                }

                if (items is not null && items.Count > 1)
                {
                    items = cloner.Clone(items) ?? throw new InvalidOperationException($"[Ledger] Shop gear clone failed for '{logicalKey}'.");
                    var root = items.FirstOrDefault(x => string.IsNullOrEmpty(x.ParentId));
                    if (root is not null)
                    {
                        var remap = new Dictionary<string, MongoId>();
                        var childIndex = 0;
                        foreach (var item in items)
                        {
                            var oldId = item.Id.ToString();
                            remap[oldId] = item == root
                                ? assortId
                                : new MongoId(StableId($"ledger-shop-{logicalKey}-part-{childIndex++:00}"));
                        }
                        foreach (var item in items)
                        {
                            var oldId = item.Id.ToString();
                            item.Id = remap[oldId];
                            if (!string.IsNullOrEmpty(item.ParentId) && remap.TryGetValue(item.ParentId, out var newParent))
                                item.ParentId = newParent.ToString();
                            item.Location = null;
                        }
                        root.ParentId = "hideout";
                        root.SlotId = "hideout";
                        root.Upd ??= new Upd();
                        root.Upd.UnlimitedCount = false;
                        root.Upd.StackObjectsCount = stock;
                        root.Upd.BuyRestrictionMax = buyLimit;
                        root.Upd.BuyRestrictionCurrent = 0;
                        assort.Items.AddRange(items);
                    }
                    else
                    {
                        logger.Warning($"[Ledger] Shop gear '{logicalKey}' build had no root; using the base item.");
                        assort.Items.Add(new Item
                        {
                            Id = assortId, Template = new MongoId(tpl), ParentId = "hideout", SlotId = "hideout",
                            Upd = new Upd { UnlimitedCount = false, StackObjectsCount = stock, BuyRestrictionMax = buyLimit, BuyRestrictionCurrent = 0 }
                        });
                    }
                }
                else
                {
                    logger.Warning($"[Ledger] No fitted shop build found for '{logicalKey}' ({tpl}); using the base item.");
                    assort.Items.Add(new Item
                    {
                        Id = assortId, Template = new MongoId(tpl), ParentId = "hideout", SlotId = "hideout",
                        Upd = new Upd { UnlimitedCount = false, StackObjectsCount = stock, BuyRestrictionMax = buyLimit, BuyRestrictionCurrent = 0 }
                    });
                }
            }
            else
            {
            assort.Items.Add(new Item
            {
                Id = assortId,
                Template = new MongoId(tpl),
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd
                {
                    UnlimitedCount = false,
                    StackObjectsCount = stock,
                    BuyRestrictionMax = buyLimit,
                    BuyRestrictionCurrent = 0
                }
            });
        }

        }

        assort.BarterScheme[assortId] = [[new BarterScheme
        {
            Count = price,
            Template = new MongoId(RoubleTemplateId)
        }]];
        assort.LoyalLevelItems[assortId] = loyalty;
    }

    private void RegisterImages()
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        for (var i = 1; i <= 6; i++)
        {
            var route = $"/files/quest/icon/ledger_quest_{i}";
            var file = System.IO.Path.Combine(modPath, "assets", "quests", $"LedgerQuest{i}.jpg");
            imageRouter.AddRoute(route, file);
        }
    }

    private void ResolveKnownItems()
    {
        foreach (var key in ItemAliases.Keys) ResolveItem(key, false);
    }

    private string? ResolveItem(string key, bool required = true)
    {
        if (_resolvedItems.TryGetValue(key, out var existing)) return existing;
        if (ExactItemIds.TryGetValue(key, out var exact) && templateTable.Items.ContainsKey(new MongoId(exact)))
        {
            _resolvedItems[key] = exact;
            return exact;
        }
        if (!ItemAliases.TryGetValue(key, out var aliases)) return null;

        if (!localeTable.Global.TryGetValue("en", out var lazy) || lazy.Value is not { } en)
        {
            if (required) logger.Error($"[Ledger] English locale unavailable while resolving '{key}'.");
            return null;
        }

        foreach (var alias in aliases)
        {
            var match = en.FirstOrDefault(kv =>
                kv.Key.EndsWith(" Name", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(kv.Value, alias, StringComparison.OrdinalIgnoreCase) &&
                IsSafeItemMatch(key, kv.Value));
            if (!string.IsNullOrEmpty(match.Key))
            {
                var id = match.Key[..^5];
                _resolvedItems[key] = id;
                return id;
            }
        }

        foreach (var alias in aliases)
        {
            // The three ambiguous rewards/objectives have extra checks below.
            // Ordinary barter items still need short aliases for locale variants.
            var match = en.FirstOrDefault(kv =>
                kv.Key.EndsWith(" Name", StringComparison.OrdinalIgnoreCase) &&
                kv.Value.Contains(alias, StringComparison.OrdinalIgnoreCase) &&
                IsSafeItemMatch(key, kv.Value));
            if (!string.IsNullOrEmpty(match.Key))
            {
                var id = match.Key[..^5];
                _resolvedItems[key] = id;
                return id;
            }
        }

        if (required) logger.Error($"[Ledger] Could not resolve item '{key}' from English locale.");
        return null;
    }

    private static bool IsSafeItemMatch(string key, string name) => key switch
    {
        "stock" => name.Contains("Magpul", StringComparison.OrdinalIgnoreCase) &&
                   name.Contains("CTR", StringComparison.OrdinalIgnoreCase) &&
                   name.Contains("stock", StringComparison.OrdinalIgnoreCase),
        "glock17" => name.Contains("pistol", StringComparison.OrdinalIgnoreCase) &&
                     !name.Contains("barrel", StringComparison.OrdinalIgnoreCase),
        "killahelmet" => name.Contains("Maska-1SCh", StringComparison.OrdinalIgnoreCase) &&
                         name.Contains("Killa", StringComparison.OrdinalIgnoreCase) &&
                         name.Contains("helmet", StringComparison.OrdinalIgnoreCase),
        _ => name.Length >= 8
    };

    private void RegisterQuests()
    {
        if (!templateTable.Quests.TryGetValue(new MongoId(VanillaQuestTemplateId), out var templateQuest) || templateQuest is null)
            throw new InvalidOperationException("[Ledger] Debut quest template not found.");

        foreach (var spec in QuestSpecs)
        {
            var quest = cloner.Clone(templateQuest) ?? throw new InvalidOperationException("Quest clone failed.");
            var questId = QuestId(spec.Number);
            quest.Id = new MongoId(questId);
            quest.QuestName = spec.Title;
            quest.TraderId = new MongoId(LedgerTraderId);
            quest.Type = QuestTypeEnum.PickUp;
            quest.Location = "any";
            quest.Image = $"/files/quest/icon/ledger_quest_{spec.Image}.jpg";
            quest.Restartable = false;
            quest.InstantComplete = spec.Number == 45;

            quest.Description = $"{questId} description";
            quest.FailMessageText = $"{questId} failMessageText";
            quest.Name = $"{questId} name";
            quest.Note = $"{questId} note";
            quest.StartedMessageText = $"{questId} startedMessageText";
            quest.SuccessMessageText = $"{questId} successMessageText";
            quest.AcceptPlayerMessage = $"{questId} acceptPlayerMessage";
            quest.DeclinePlayerMessage = $"{questId} declinePlayerMessage";
            quest.CompletePlayerMessage = $"{questId} completePlayerMessage";

            quest.Conditions.AvailableForStart = BuildStartConditions(spec.Number);
            quest.Conditions.Fail = [];
            quest.Conditions.AvailableForFinish = BuildFinishConditions(spec);

            quest.Rewards!.Clear();
            quest.Rewards["Started"] = [];
            quest.Rewards["Success"] = BuildRewards(spec);
            quest.Rewards["Fail"] = [];

            templateTable.Quests[new MongoId(questId)] = quest;
            AddQuestLocales(spec, questId);
        }
    }

    private List<QuestCondition> BuildStartConditions(int q)
    {
        var list = new List<QuestCondition>();
        if (q > 1)
        {
            list.Add(new QuestCondition
            {
                ConditionType = "Quest", DynamicLocale = false, GlobalQuestCounterId = string.Empty,
                Id = new MongoId(ConditionId(q, 90)), Index = 0, ParentId = string.Empty, AvailableAfter = 0,
                Dispersion = 0, Target = new ListOrT<string>(null!, QuestId(q - 1)),
                Status = [QuestStatusEnum.Success], VisibilityConditions = []
            });
        }

        if (q == 45)
        {
            list.Add(new QuestCondition
            {
                ConditionType = "TraderLoyalty", DynamicLocale = false, GlobalQuestCounterId = string.Empty,
                Id = new MongoId(ConditionId(q, 91)), Index = 1, ParentId = string.Empty,
                CompareMethod = ">=", Target = new ListOrT<string>(null!, LedgerTraderId), Value = 4,
                VisibilityConditions = []
            });
        }
        return list;
    }

    private List<QuestCondition> BuildFinishConditions(QuestSpec spec)
    {
        if (spec.Number == 45)
        {
            // Final letter/claim quest: Q44 + LL4 are checked before it appears, and InstantComplete
            // makes accepting the quest immediately eligible for its final message and rewards.
            return [];
        }

        var conditions = new List<QuestCondition>();
        var index = 0;
        foreach (var o in spec.Objectives)
        {
            if (o.Kind == "dog")
            {
                var ids = o.Key switch { "bear" => BearDogtags, "usec" => UsecDogtags, _ => AllDogtags };
                conditions.Add(new QuestCondition
                {
                    ConditionType = "HandoverItem", DogtagLevel = o.Level, DynamicLocale = false,
                    GlobalQuestCounterId = string.Empty, Id = new MongoId(ConditionId(spec.Number, index + 1)),
                    Index = index, IsEncoded = false, MaxDurability = 100, MinDurability = 0,
                    OnlyFoundInRaid = false, ParentId = string.Empty,
                    Target = new ListOrT<string>(ids, null!), Value = o.Count, VisibilityConditions = []
                });
            }
            else
            {
                var tpl = ResolveItem(o.Key);
                if (tpl is null) throw new InvalidOperationException($"[Ledger] Required objective item '{o.Key}' could not be resolved for Q{spec.Number}.");
                conditions.Add(new QuestCondition
                {
                    ConditionType = "HandoverItem", DogtagLevel = 0, DynamicLocale = false,
                    GlobalQuestCounterId = string.Empty, Id = new MongoId(ConditionId(spec.Number, index + 1)),
                    Index = index, IsEncoded = false, MaxDurability = 100, MinDurability = 0,
                    OnlyFoundInRaid = false, ParentId = string.Empty,
                    Target = new ListOrT<string>([tpl], null!), Value = o.Count, VisibilityConditions = []
                });
            }
            index++;
        }
        return conditions;
    }

    private List<Reward> BuildRewards(QuestSpec spec)
    {
        var rewards = new List<Reward>();
        _activeRewardQuest = spec.Number;
        var index = 0;
        AddExperienceReward(rewards, spec.Xp / 5, ref index);
        if (spec.Cash > 0) AddRawItemReward(rewards, RoubleTemplateId, spec.Cash, ref index, stack: true);
        if (spec.Rep > 0) AddStandingReward(rewards, spec.Rep, ref index);
        AddSpecialRewards(spec.Number, rewards, ref index);
        return rewards;
    }

    private void AddExperienceReward(List<Reward> rewards, int xp, ref int index) =>
        rewards.Add(new Reward { Id = new MongoId(RewardId(_activeRewardQuest, index + 1)), Index = index++, Type = RewardType.Experience,
            Value = xp, Unknown = true, AvailableInGameEditions = [] });

    private void AddStandingReward(List<Reward> rewards, double value, ref int index) =>
        rewards.Add(new Reward { Id = new MongoId(RewardId(_activeRewardQuest, index + 1)), Index = index++, Type = RewardType.TraderStanding,
            Target = LedgerTraderId, Value = value, Unknown = true, AvailableInGameEditions = [] });

    private void AddItemReward(List<Reward> rewards, string logicalKey, int count, ref int index)
    {
        var tpl = ResolveItem(logicalKey, false);
        if (tpl is null)
        {
            logger.Warning($"[Ledger] Reward '{logicalKey}' was not resolved; skipping it.");
            return;
        }
        AddRawItemReward(rewards, tpl, count, ref index, StackableRewardKeys.Contains(logicalKey));
    }

    private void AddRawItemReward(List<Reward> rewards, string tpl, int count, ref int index, bool stack = false)
    {
        var items = new List<Item>();
        if (stack)
        {
            items.Add(new Item
            {
                Id = new MongoId(StableId($"ledger-item-{_activeRewardQuest:00}-{index + 1:00}-00")),
                Template = new MongoId(tpl),
                Upd = new Upd { StackObjectsCount = count }
            });
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                items.Add(new Item
                {
                    Id = new MongoId(StableId($"ledger-item-{_activeRewardQuest:00}-{index + 1:00}-{i:00}")),
                    Template = new MongoId(tpl),
                    Upd = new Upd { StackObjectsCount = 1 }
                });
            }
        }

        rewards.Add(new Reward
        {
            Id = new MongoId(RewardId(_activeRewardQuest, index + 1)),
            Index = index++,
            Type = RewardType.Item,
            Value = count,
            Target = items[0].Id.ToString(),
            FindInRaid = false,
            Unknown = true,
            AvailableInGameEditions = [],
            Items = items
        });
    }

    private void AddPresetReward(List<Reward> rewards, string logicalKey, int count, ref int index)
    {
        var tpl = ResolveItem(logicalKey, false);
        if (tpl is null)
        {
            if (logicalKey == "mp5")
            {
                logger.Warning("[Ledger] MP5 not resolved; substituting assembled Glock 17s.");
                AddPresetReward(rewards, "glock17", count, ref index);
                return;
            }
            logger.Warning($"[Ledger] Preset reward '{logicalKey}' was not resolved; skipping it.");
            return;
        }
        if (logicalKey is "class6armor" or "helmet" or "osprey" or "thor" or "slick" or "ulach" or "fastmt" or "caiman")
        {
            AddFittedGearReward(rewards, logicalKey, tpl, count, ref index);
            return;
        }

        for (var c = 0; c < count; c++)
        {
            var preset = presetHelper.GetDefaultPreset(new MongoId(tpl));
            var items = preset?.Items.Where(x => x.Template.ToString() == tpl || x.ParentId is not null).ToList();
            var traderBuild = FindTraderWeaponBuild(tpl);
            if (traderBuild is not null && traderBuild.Count > (items?.Count ?? 0)) items = traderBuild;
            if (items is null || items.Count < 4)
            {
                if (logicalKey == "mp5")
                {
                    logger.Warning("[Ledger] No complete MP5 build; substituting an assembled Glock 17.");
                    AddPresetReward(rewards, "glock17", 1, ref index);
                    continue;
                }
                logger.Warning($"[Ledger] No complete weapon build for '{logicalKey}' ({tpl}); skipping bare receiver reward.");
                continue;
            }

            items = cloner.Clone(items) ?? throw new InvalidOperationException($"[Ledger] Weapon build clone failed for '{logicalKey}'.");
            var root = items.FirstOrDefault(x => x.Template.ToString() == tpl);
            if (root is null)
            {
                logger.Warning($"[Ledger] Weapon build for '{logicalKey}' has no matching root; skipping.");
                continue;
            }
            if (_activeRewardQuest == 25)
                ImproveSecondOpinionBuild(logicalKey, root, items);
            var rewardIndex = index + 1;
            var remap = items.Select((x, n) => (x, n)).ToDictionary(
                pair => pair.x.Id.ToString(), pair => new MongoId(StableId($"ledger-preset-{_activeRewardQuest:00}-{rewardIndex:00}-{c:00}-{pair.n:00}")));
            foreach (var item in items)
            {
                var old = item.Id.ToString();
                item.Id = remap[old];
                if (!string.IsNullOrEmpty(item.ParentId) && remap.TryGetValue(item.ParentId, out var newParent))
                    item.ParentId = newParent.ToString();
                item.Location = null;
            }
            root.ParentId = null;
            root.SlotId = null;
            root.Upd ??= new Upd();
            root.Upd.UnlimitedCount = false;
            root.Upd.StackObjectsCount = 1;
            rewards.Add(new Reward
            {
                Id = new MongoId(RewardId(_activeRewardQuest, index + 1)), Index = index++, Type = RewardType.Item, Value = 1,
                Target = root.Id.ToString(), FindInRaid = false, Unknown = true, AvailableInGameEditions = [], Items = items
            });
        }
    }


    private List<Item>? BuildKnownFittedGear(string logicalKey, string resolvedTpl)
    {
        // Release-safe fitted gear builds captured from working SPT 4.1.6 profiles.
        // Where the originally requested item had no usable preset, use a close, complete
        // vanilla equivalent rather than handing out an empty/red armour item.
        string rootTpl;
        (string tpl, string slot)[] children;

        switch (logicalKey)
        {
            case "slick":
                rootTpl = "5e4abb5086f77406975c9342";
                children =
                [
                    ("6575e71760703324250610c3", "Soft_armor_front"),
                    ("6575e72660703324250610c7", "Soft_armor_back"),
                    ("656fa76500d62bcd2e024080", "Front_plate"),
                    ("656fa76500d62bcd2e024080", "Back_plate")
                ];
                break;

            case "caiman":
                rootTpl = "5f60b34a41e30a4ab12a6947";
                children =
                [
                    ("657bbb31b30eca9763051183", "Helmet_back"),
                    ("657bbad7a1c61ee0c3036323", "Helmet_top")
                ];
                break;

            case "fastmt":
                rootTpl = "5ea17ca01412a1425304d1c0";
                children =
                [
                    ("657f9a94ada5fadd1f07a589", "Helmet_back"),
                    ("657f9a55c6679fefb3051e19", "Helmet_top")
                ];
                break;

            case "ulach":
                // ULACH itself has no usable fitted preset in 4.1.6; substitute a
                // complete FAST MT build verified in the live profile.
                rootTpl = "5ea17ca01412a1425304d1c0";
                children =
                [
                    ("657f9a94ada5fadd1f07a589", "Helmet_back"),
                    ("657f9a55c6679fefb3051e19", "Helmet_top")
                ];
                break;

            case "thor":
            case "osprey":
                // Both original roots lack usable fitted presets here.  Zhuk-6a is a
                // complete vanilla high-tier armour build verified in the test profile.
                rootTpl = "5c0e625a86f7742d77340f62";
                children =
                [
                    ("657643a220cc24d17102b14c", "Collar"),
                    ("657643732bc38ef78e076477", "soft_armor_right"),
                    ("6576434820cc24d17102b148", "Soft_armor_left"),
                    ("657642b0e6d5dd75f40688a5", "Soft_armor_back"),
                    ("65764275d8537eb26a0355e9", "Soft_armor_front"),
                    ("64afd81707e2cf40e903a316", "Right_side_plate"),
                    ("64afd81707e2cf40e903a316", "Left_side_plate"),
                    ("656fafe3498d1b7e3e071da4", "Back_plate"),
                    ("656f63c027aed95beb08f62c", "Front_plate")
                ];
                break;

            default:
                return null;
        }

        var rootId = new MongoId(StableId($"ledger-known-gear-root-{_activeRewardQuest:00}-{logicalKey}"));
        var items = new List<Item>
        {
            new()
            {
                Id = rootId,
                Template = new MongoId(rootTpl),
                Upd = new Upd { StackObjectsCount = 1 }
            }
        };

        for (var i = 0; i < children.Length; i++)
        {
            items.Add(new Item
            {
                Id = new MongoId(StableId($"ledger-known-gear-child-{_activeRewardQuest:00}-{logicalKey}-{i:00}")),
                Template = new MongoId(children[i].tpl),
                ParentId = rootId.ToString(),
                SlotId = children[i].slot
            });
        }

        return items;
    }

    private void AddFittedGearReward(List<Reward> rewards, string logicalKey, string tpl, int count, ref int index)
    {
        for (var c = 0; c < count; c++)
        {
            var items = BuildKnownFittedGear(logicalKey, tpl);
            if (items is null)
            {
                var preset = presetHelper.GetDefaultPreset(new MongoId(tpl));
                items = preset?.Items.Where(x => x.Template.ToString() == tpl || x.ParentId is not null).ToList();
            }

            if (items is null || items.Count <= 1)
            {
                logger.Warning($"[Ledger] No fitted build found for '{logicalKey}' ({tpl}); using the base item as a safe fallback.");
                AddRawItemReward(rewards, tpl, 1, ref index);
                continue;
            }

            items = cloner.Clone(items) ?? throw new InvalidOperationException($"[Ledger] Gear preset clone failed for '{logicalKey}'.");
            var root = items.FirstOrDefault(x => string.IsNullOrEmpty(x.ParentId));
            if (root is null)
            {
                logger.Warning($"[Ledger] Fitted preset for '{logicalKey}' has no matching root; using the base item.");
                AddRawItemReward(rewards, tpl, 1, ref index);
                continue;
            }

            var rewardIndex = index + 1;
            var remap = items.Select((x, n) => (x, n)).ToDictionary(
                pair => pair.x.Id.ToString(),
                pair => new MongoId(StableId($"ledger-gear-{_activeRewardQuest:00}-{rewardIndex:00}-{c:00}-{pair.n:00}")));

            foreach (var item in items)
            {
                var old = item.Id.ToString();
                item.Id = remap[old];
                if (!string.IsNullOrEmpty(item.ParentId) && remap.TryGetValue(item.ParentId, out var newParent))
                    item.ParentId = newParent.ToString();
                item.Location = null;
            }

            root.ParentId = null;
            root.SlotId = null;
            root.Upd ??= new Upd();
            root.Upd.UnlimitedCount = false;
            root.Upd.StackObjectsCount = 1;

            rewards.Add(new Reward
            {
                Id = new MongoId(RewardId(_activeRewardQuest, index + 1)), Index = index++, Type = RewardType.Item, Value = 1,
                Target = root.Id.ToString(), FindInRaid = false, Unknown = true, AvailableInGameEditions = [], Items = items
            });
        }
    }

    private void ImproveSecondOpinionBuild(string logicalKey, Item root, List<Item> items)
    {
        if (logicalKey == "m4a1")
        {
            // The SOPMOD II default build puts its PEQ on the upper/optic side of the RIS II.
            // The left rail is empty in this preset and accepts the same device.
            var handguard = items.FirstOrDefault(x => x.Template.ToString() == "55f84c3c4bdc2d5f408b4576");
            var laser = items.FirstOrDefault(x => x.Template.ToString() == "544909bb4bdc2d6f028b4577"
                && x.ParentId == handguard?.Id.ToString() && x.SlotId == "mod_tactical");
            if (laser is not null && !items.Any(x => x.ParentId == handguard!.Id.ToString() && x.SlotId == "mod_tactical001"))
                laser.SlotId = "mod_tactical001";
        }
        else if (logicalKey == "m1a" && !items.Any(x => x.ParentId == root.Id.ToString() && x.SlotId == "mod_mount"))
        {
            // The M1A receiver accepts the M14 A.R.M.S. #18 rail. Add a direct-mount
            // SpecterDR so the Second Opinion reward arrives ready to aim and use.
            const string mountTpl = "5addbfe15acfc4001a5fc58b";
            const string opticTpl = "57ac965c24597706be5f975c";
            if (!templateTable.Items.ContainsKey(new MongoId(mountTpl)) || !templateTable.Items.ContainsKey(new MongoId(opticTpl)))
            {
                logger.Warning("[Ledger] Q25 M1A optic parts unavailable; retaining the complete base rifle.");
                return;
            }
            var mount = new Item { Id = new MongoId(StableId("ledger-q25-m1a-mount")), Template = new MongoId(mountTpl),
                ParentId = root.Id.ToString(), SlotId = "mod_mount" };
            var optic = new Item { Id = new MongoId(StableId("ledger-q25-m1a-optic")), Template = new MongoId(opticTpl),
                ParentId = mount.Id.ToString(), SlotId = "mod_scope" };
            items.Add(mount);
            items.Add(optic);
        }
    }

    private List<Item>? FindTraderWeaponBuild(string tpl)
    {
        List<Item>? best = null;
        foreach (var trader in tradersTable.Values)
        {
            var assortment = trader.Assort?.Items;
            if (assortment is null) continue;
            foreach (var root in assortment.Where(x => x.Template.ToString() == tpl))
            {
                var build = new List<Item> { root };
                var ids = new HashSet<string> { root.Id.ToString() };
                var added = true;
                while (added)
                {
                    added = false;
                    foreach (var part in assortment)
                    {
                        if (ids.Contains(part.Id.ToString()) || part.ParentId is null || !ids.Contains(part.ParentId)) continue;
                        build.Add(part);
                        ids.Add(part.Id.ToString());
                        added = true;
                    }
                }
                if (build.Count > (best?.Count ?? 0)) best = build;
            }
        }
        return best;
    }

    private void AddSpecialRewards(int q, List<Reward> rewards, ref int index)
    {
        switch (q)
        {
            case 1:
                AddItemReward(rewards, "beer", 1, ref index);
                break;
            case 2:
                AddItemReward(rewards, "tea", 1, ref index); AddItemReward(rewards, "alyonka", 1, ref index);
                break;
            case 3:
                AddItemReward(rewards, "vodka", 1, ref index); AddItemReward(rewards, "m855a1", 30, ref index);
                break;
            case 4:
                AddItemReward(rewards, "crackers", 3, ref index); AddItemReward(rewards, "condensedmilk", 1, ref index);
                break;
            case 5:
                AddItemReward(rewards, "tushonka", 4, ref index); AddItemReward(rewards, "toiletpaper", 3, ref index); AddItemReward(rewards, "magnum", 40, ref index);
                break;
            case 6:
                AddItemReward(rewards, "m80", 17, ref index); AddItemReward(rewards, "water", 2, ref index); AddItemReward(rewards, "lightbulb", 4, ref index);
                break;
            case 7:
                AddItemReward(rewards, "pbp", 90, ref index); AddItemReward(rewards, "screwdriver", 1, ref index); AddItemReward(rewards, "sugar", 2, ref index); AddItemReward(rewards, "pliers", 1, ref index); AddItemReward(rewards, "measuringtape", 1, ref index);
                break;
            case 8:
                AddItemReward(rewards, "545bp", 60, ref index); AddItemReward(rewards, "weaponparts", 2, ref index); AddItemReward(rewards, "bolts", 3, ref index); AddItemReward(rewards, "screwnuts", 3, ref index);
                break;
            case 9:
                AddItemReward(rewards, "762ps", 120, ref index); AddItemReward(rewards, "ducttape", 4, ref index); AddItemReward(rewards, "insulatingtape", 2, ref index); AddItemReward(rewards, "peas", 4, ref index);
                break;
            case 10:
                AddItemReward(rewards, "holo", 1, ref index); AddItemReward(rewards, "laser_dbal", 1, ref index); AddItemReward(rewards, "grip_rk2", 1, ref index); AddItemReward(rewards, "stock", 1, ref index); AddItemReward(rewards, "suppressor_saker", 1, ref index);
                break;
            case 11:
                AddItemReward(rewards, "grizzly", 1, ref index); AddItemReward(rewards, "ifak", 2, ref index); AddItemReward(rewards, "alusplint", 2, ref index); AddItemReward(rewards, "suppressor_hybrid", 1, ref index); AddItemReward(rewards, "propital", 2, ref index);
                break;
            case 12:
                AddItemReward(rewards, "salewa", 2, ref index); AddItemReward(rewards, "m80", 80, ref index); AddItemReward(rewards, "gunoil", 3, ref index);
                break;
            case 13:
                AddPresetReward(rewards, "rsass", 1, ref index); AddItemReward(rewards, "x25", 3, ref index); AddItemReward(rewards, "m61", 150, ref index);
                break;
            case 14:
                AddItemReward(rewards, "propital", 2, ref index); AddItemReward(rewards, "76254high", 120, ref index); AddPresetReward(rewards, "thor", 1, ref index); AddPresetReward(rewards, "caiman", 1, ref index);
                break;
            case 15:
                AddItemReward(rewards, "afak", 2, ref index); AddItemReward(rewards, "cms", 1, ref index); AddItemReward(rewards, "zagustin", 2, ref index); AddItemReward(rewards, "m855a1", 180, ref index); AddPresetReward(rewards, "fastmt", 1, ref index);
                break;
            case 16:
                AddItemReward(rewards, "dogtagcase", 1, ref index);
                break;
            case 17:
                AddItemReward(rewards, "propital", 2, ref index); AddItemReward(rewards, "ifak", 2, ref index); AddItemReward(rewards, "water", 1, ref index);
                break;
            case 18:
                AddPresetReward(rewards, "osprey", 1, ref index); AddItemReward(rewards, "pmag60", 4, ref index); AddItemReward(rewards, "m855a1", 240, ref index);
                break;
            case 19:
                AddItemReward(rewards, "headset", 2, ref index); AddItemReward(rewards, "grenade", 8, ref index); AddItemReward(rewards, "propital", 3, ref index); AddItemReward(rewards, "zagustin", 2, ref index); AddItemReward(rewards, "etgc", 2, ref index);
                break;
            case 20:
                AddItemReward(rewards, "pmag60", 3, ref index); AddItemReward(rewards, "m855a1", 180, ref index); AddItemReward(rewards, "bandage", 2, ref index); AddItemReward(rewards, "alusplint", 2, ref index);
                break;
            case 21:
                AddItemReward(rewards, "etgc", 2, ref index); AddItemReward(rewards, "zagustin", 2, ref index); AddItemReward(rewards, "surv12", 2, ref index); AddPresetReward(rewards, "slick", 1, ref index); AddPresetReward(rewards, "ulach", 1, ref index); AddItemReward(rewards, "m61", 120, ref index);
                break;
            case 22:
                AddItemReward(rewards, "labscard", 1, ref index); AddItemReward(rewards, "m62", 120, ref index); AddItemReward(rewards, "aquamari", 4, ref index);
                break;
            case 23:
                AddItemReward(rewards, "ap20", 80, ref index); AddItemReward(rewards, "magnum", 80, ref index); AddItemReward(rewards, "76239high", 66, ref index); AddItemReward(rewards, "cms", 1, ref index);
                break;
            case 24:
                AddPresetReward(rewards, "glock17", 1, ref index); AddItemReward(rewards, "pbp", 200, ref index); AddItemReward(rewards, "glockmag", 4, ref index); AddItemReward(rewards, "bitcoin", 1, ref index);
                break;
            case 25:
                AddPresetReward(rewards, "m4a1", 1, ref index); AddItemReward(rewards, "reapir", 1, ref index); AddPresetReward(rewards, "m1a", 1, ref index); AddItemReward(rewards, "m855a1", 240, ref index); AddItemReward(rewards, "m61", 120, ref index);
                break;
            case 26:
                AddPresetReward(rewards, "aks74u", 2, ref index); AddPresetReward(rewards, "mp5", 2, ref index); AddPresetReward(rewards, "mp133", 2, ref index); AddPresetReward(rewards, "toz", 1, ref index); AddPresetReward(rewards, "vpo", 1, ref index);
                break;
            case 27:
                AddItemReward(rewards, "suppressor_nt4", 1, ref index); AddItemReward(rewards, "suppressor_sdn6", 1, ref index); AddItemReward(rewards, "suppressor_osprey9", 1, ref index);
                AddItemReward(rewards, "optic_razor", 1, ref index); AddItemReward(rewards, "optic_valday", 1, ref index); AddItemReward(rewards, "optic_vudu", 1, ref index); AddItemReward(rewards, "optic_specter", 1, ref index);
                AddItemReward(rewards, "grip_rvg", 1, ref index); AddItemReward(rewards, "grip_se5", 1, ref index); AddItemReward(rewards, "grip_rk1", 1, ref index);
                AddItemReward(rewards, "laser_peq15", 1, ref index); AddItemReward(rewards, "laser_x400", 1, ref index); AddItemReward(rewards, "laser_klesch", 1, ref index);
                AddItemReward(rewards, "m855a1", 180, ref index); AddItemReward(rewards, "m61", 80, ref index);
                break;
            case 28:
                AddItemReward(rewards, "rbvo", 1, ref index); AddItemReward(rewards, "vpx", 1, ref index); AddItemReward(rewards, "cofdm", 1, ref index);
                break;
            case 29:
                AddItemReward(rewards, "dorm314", 1, ref index); AddItemReward(rewards, "virtex", 1, ref index); AddItemReward(rewards, "phasearray", 1, ref index); AddItemReward(rewards, "gpu", 1, ref index);
                break;
            case 30:
                AddItemReward(rewards, "ssd", 1, ref index); AddItemReward(rewards, "milcable", 1, ref index); AddItemReward(rewards, "mpfilter", 1, ref index); AddItemReward(rewards, "rfid", 1, ref index); AddItemReward(rewards, "mcb", 1, ref index); AddItemReward(rewards, "iridium", 1, ref index);
                break;
            case 31:
                AddItemReward(rewards, "water", 3, ref index); AddItemReward(rewards, "food", 3, ref index); AddItemReward(rewards, "salewa", 2, ref index); AddItemReward(rewards, "alusplint", 2, ref index);
                break;
            case 32:
                AddItemReward(rewards, "rbst", 1, ref index); AddItemReward(rewards, "flashdrive", 3, ref index); AddItemReward(rewards, "docscase", 1, ref index);
                break;
            case 33:
                AddItemReward(rewards, "ibuprofen", 3, ref index); AddItemReward(rewards, "goldenstar", 2, ref index); AddItemReward(rewards, "propital", 2, ref index); AddItemReward(rewards, "ifak", 2, ref index);
                break;
            case 34:
                AddItemReward(rewards, "factorykey", 1, ref index); AddItemReward(rewards, "etgc", 2, ref index); AddItemReward(rewards, "alusplint", 2, ref index);
                break;
            case 35:
                AddItemReward(rewards, "vodka", 3, ref index); AddItemReward(rewards, "cigarettes", 3, ref index); AddItemReward(rewards, "ap20", 120, ref index); AddItemReward(rewards, "zagustin", 2, ref index);
                break;
            case 36:
                AddPresetReward(rewards, "goldentt", 1, ref index); AddItemReward(rewards, "labsviolet", 1, ref index); AddItemReward(rewards, "bitcoin", 1, ref index); AddItemReward(rewards, "magcase", 1, ref index);
                break;
            case 37:
                AddItemReward(rewards, "killahelmet", 1, ref index); AddItemReward(rewards, "545bt", 200, ref index); AddItemReward(rewards, "rbak", 1, ref index); AddItemReward(rewards, "surv12", 2, ref index); AddItemReward(rewards, "etgc", 2, ref index);
                break;
            case 38:
                AddItemReward(rewards, "itemcase", 1, ref index); AddItemReward(rewards, "grizzly", 2, ref index); AddItemReward(rewards, "cms", 2, ref index); AddItemReward(rewards, "m62", 160, ref index); AddItemReward(rewards, "545bs", 180, ref index);
                break;
            case 39:
                AddPresetReward(rewards, "g28", 1, ref index); AddPresetReward(rewards, "spear", 1, ref index); AddItemReward(rewards, "m61", 200, ref index); AddItemReward(rewards, "m855a1", 200, ref index);
                break;
            case 40:
                AddItemReward(rewards, "rbbk", 1, ref index); AddItemReward(rewards, "rbpkpm", 1, ref index); AddItemReward(rewards, "chek15", 1, ref index); AddItemReward(rewards, "abandonedmarked", 1, ref index); AddItemReward(rewards, "bitcoin", 1, ref index);
                break;
            case 41:
                AddItemReward(rewards, "medcase", 1, ref index); AddItemReward(rewards, "grizzly", 2, ref index); AddItemReward(rewards, "surv12", 2, ref index); AddItemReward(rewards, "zagustin", 3, ref index); AddItemReward(rewards, "etgc", 2, ref index); AddItemReward(rewards, "338fmj", 40, ref index);
                break;
            case 42:
                AddItemReward(rewards, "weaponcase", 1, ref index); AddItemReward(rewards, "afak", 3, ref index); AddItemReward(rewards, "cms", 2, ref index); AddItemReward(rewards, "propital", 3, ref index); AddItemReward(rewards, "zagustin", 3, ref index); AddItemReward(rewards, "etgc", 3, ref index);
                break;
            case 43:
                AddItemReward(rewards, "m61", 240, ref index); AddItemReward(rewards, "m855a1", 360, ref index); AddItemReward(rewards, "ap20", 160, ref index); AddItemReward(rewards, "545bs", 240, ref index); AddItemReward(rewards, "bitcoin", 1, ref index);
                break;
            case 44:
                AddItemReward(rewards, "labsgreen", 1, ref index); AddItemReward(rewards, "sicccase", 1, ref index); AddItemReward(rewards, "bitcoin", 2, ref index);
                break;
            case 45:
                AddItemReward(rewards, "dogtagcase", 1, ref index); AddItemReward(rewards, "labsred", 1, ref index); AddItemReward(rewards, "ammocase", 1, ref index); AddItemReward(rewards, "thiccweaponcase", 1, ref index); AddItemReward(rewards, "thiccitemcase", 1, ref index); AddItemReward(rewards, "338fmj", 120, ref index); AddItemReward(rewards, "bitcoin", 5, ref index); AddPresetReward(rewards, "mk18", 1, ref index);
                break;
        }
    }

    private static readonly Dictionary<int, string> QuestStartedMessages = new()
    {
        [1] = "Good. One will do.",
        [2] = "Good. One of each. Keep them separate.",
        [3] = "Level twenty-five or higher. I will notice.",
        [4] = "Five. Any five. This should be straightforward.",
        [5] = "Four BEAR. Two USEC. In that ratio.",
        [6] = "Seventeen or higher. Four of them.",
        [7] = "Seven tags and the beer. The bottle is the important part.",
        [8] = "Nine. And remind me about the box under the desk.",
        [9] = "Fifteen. Try not to make a hobby of this.",
        [10] = "Coffee first. Five tags second. Preferably in that order.",
        [11] = "Five and five. Symmetry matters.",
        [12] = "Three BEAR. Eight USEC. Yes, deliberately uneven.",
        [13] = "Five USEC. Level twenty-three or higher. Please hurry.",
        [14] = "Ten experienced operators. Level thirty or higher.",
        [15] = "Six and six. Same threshold. Different training.",
        [16] = "Forty. Yes, forty. We have discussed your hoarding.",
        [17] = "Three this time. See? Perfectly reasonable.",
        [18] = "One USEC. Four BEAR. Keep the groups distinct.",
        [19] = "Six USEC. Two BEAR. Level twenty-eight or higher.",
        [20] = "Three USEC. Seven BEAR. Level thirty-two or higher.",
        [21] = "Four and four. This one is about what happens after the plan fails.",
        [22] = "Seven BEAR. Nine USEC. No scenarios this time.",
        [23] = "Six tags. Level thirty-three or higher. That should correct it.",
        [24] = "A standard Glock. Standard means unmodified.",
        [25] = "Five tags. Then I want your opinion.",
        [26] = "Eight tags. I have some things to clear out.",
        [27] = "Three BEAR. Five USEC. Something familiar.",
        [28] = "One graphics card and five tags. Apparently this is progress.",
        [29] = "Cable, power filter, six tags. I have a list now.",
        [30] = "Nine tags. No computers.",
        [31] = "Three. Any three. Read the names before you hand them over.",
        [32] = "Paper, flash drives and five tags. Paper cannot crash.",
        [33] = "Seven tags and the painkillers. My hand is fine.",
        [34] = "The glasses, painkillers and six tags. Do not comment on the lighting.",
        [35] = "Eight tags, cigarettes and vodka. I am broadening the sample.",
        [36] = "The golden pistol and five tags. I want to know who chose it.",
        [37] = "Ten tags, food, drink and cigarettes. Ordinary things matter too.",
        [38] = "Twenty. And no, you are not getting another case.",
        [39] = "Just one. Choose it properly.",
        [40] = "Five more. I am clearing a few things up.",
        [41] = "Three tags and an energy drink. Do not make anything of it.",
        [42] = "Water, energy, the bear and one tag. Yes, the bear too.",
        [43] = "One tag and some paper. I need to write something down.",
        [44] = "Two beers. One for you and one for me. Do not make a thing of it.",
        [45] = "There is nothing to bring me this time."
    };

    // A second paragraph for the task screen. The shorter acceptance and completion
    // messages stay as they are, and the final name remains exclusive to Q45 success.
    private static readonly Dictionary<int, string> QuestBriefingDetails = new()
    {
        [1] = "A tag can tell me where its owner came from, but the interesting part is what happened afterward. Even a scuffed edge can place a person somewhere a tidy official record never will. Start with one, and let me see what your part of Tarkov leaves behind.",
        [2] = "I have two trays on the desk. People keep assuring me the factions are interchangeable once the fighting starts, but their equipment tells a less convenient story. Place one in each tray. I want to look at them side by side before I draw any conclusions.",
        [3] = "You can learn a great deal from somebody who has been here long enough to replace their original kit. Repairs accumulate. Habits become visible. I suspect their tags will show the same thing, though I would prefer evidence to a hunch.",
        [4] = "The empty spaces are numbered, which was a sensible system until I began looking at them instead of the objects around them. Now I cannot concentrate. Fill this section and I may finally be able to work on something else.",
        [5] = "I found the ratio in a report that was more interested in victory than the people involved. The figures ought to be checked against something real. I will explain what I am looking for when I know whether the report was worth keeping.",
        [6] = "My notes suggest a curious change around that point. Before it, people seem to rely on whatever they were issued. After it, they begin making choices of their own. I want four examples before I decide whether that is a pattern or simply a pleasing story.",
        [7] = "There is a stain on my ledger where I set the original bottle down. It has made a surprisingly persuasive argument for correcting that transaction. The tags are a separate matter, though I may as well record both debts at once.",
        [8] = "The box beneath my desk is marked ammunition, but I have not opened it since it arrived. There are several equally mysterious parcels beside it. Help me finish the next row, and you can take the whole obstacle away.",
        [9] = "Individual stories can mislead you. Fifteen may be enough to show me which marks occur by chance and which appear again and again. I have cleared a table for them, although I may have underestimated the amount of table required.",
        [10] = "I have been sorting names into columns all night and twice caught myself writing the same one. That is unacceptable. The coffee is for me; the experienced tags are for the record. If the date written on the wall is correct, this is also an excellent excuse to call the exchange a celebration.",
        [11] = "A balanced sample ought to make comparison easier. I want to see whether two groups with similar experience chose different ways to stay alive. Ten records will make a fair beginning. You, meanwhile, should consider taking the medical equipment I have accumulated.",
        [12] = "The last arrangement was tidy enough to frame and almost useless for answering the question. Real evidence rarely presents itself in matching columns. This time I intend to leave the imbalance exactly as it is and see what it tells me.",
        [13] = "I have checked the shelves, the envelopes and the floor. I was not robbed. I simply wrote down five names before I had five tags. It is an embarrassing error and one I would appreciate resolving before anybody else notices the labels.",
        [14] = "Experience is often treated as proof of good judgment. It may only prove that somebody survived long enough to make more decisions. I want to know whether the older tags share anything beyond their numbers, even if the answer is uncomfortable.",
        [15] = "A larger sample failed to explain why some people last and others do not. Training is the next thing to test. Keep the factions separate, but hold the threshold steady. If the results still refuse to cooperate, I will have to admit the question is harder than I thought.",
        [16] = "You have been glancing at that case every time you come in. I recognise the look; it is the one I used to give my first cabinet. Collections need records and limits. Forty ought to establish whether yours has either.",
        [17] = "I reviewed our last exchange and decided that forty may have been a little much. I am not apologising for the audit; the result was informative. This request is smaller, and the payment is considerably better. Make of that what you will.",
        [18] = "I have drawn the roads and buildings on an old sheet of paper. The isolated man has cover for perhaps a minute before four others can reach him. Different levels of experience would ruin the exercise, so find me people who have at least seen a few fights.",
        [19] = "The first arrangement was too simple. Reinforcements change the lines of sight, the ammunition needed and the temptation to retreat. I have moved the pieces twice already. Bring the next group and I will find out whether my survivor still deserves the name.",
        [20] = "I counted the rounds each defender could carry and then counted the approaches. The sums do not agree. I have three magazines on the table to demonstrate precisely what should have been packed; perhaps that will persuade you to check your own kit before leaving.",
        [21] = "A plan that only works while everyone behaves as expected is a poor plan. I am adding injuries and a blocked exit to the board. Equal groups will keep the comparison useful. What matters now is whether anyone can adapt quickly enough to leave.",
        [22] = "I caught myself moving a piece back onto the board because I disliked where it had fallen. That is not analysis. The board is put away. Bring me records with firm numbers, and I will write down what I find without trying to improve the ending.",
        [23] = "The correction is in a footnote nobody else would read. I read it, of course. Six additional records should bring the totals into line. Whether a better total leads to a better conclusion is a question I intend to postpone until tomorrow.",
        [24] = "People describe a pistol as if every part were an argument. I would like to see the unaltered object before hearing another opinion about it. The tags provide a familiar control group, if that makes this sudden interest sound more respectable.",
        [25] = "I took the pistol apart, put it back together, and somehow ended up reading about thermal sights. I have prepared two rifles for your inspection. One is meant for close work; the other is built for distance. The comparison may tell me more than another evening with a parts catalogue.",
        [26] = "Once people discovered I could identify a weapon, they began leaving unwanted ones here. The pile has occupied the chair where visitors are supposed to sit. Take the lot as payment for the records, and I might remember what the room looked like before this experiment.",
        [27] = "I can tell you exactly which parts would fit the weapons in the other room, and I resent having learned it. The drawer is full of attachments I bought while trying to settle an argument with myself. Please take them before I start another one.",
        [28] = "The old machine has my catalogue, correspondence and several years of corrections. Its appearance has never bothered me. Being told it is obsolete has bothered me considerably. Bring the replacement component and I will discover whether the criticism was justified.",
        [29] = "I followed the instructions as far as the word 'install', then discovered that replacing one part apparently requires replacing half the room. The cable and filter are on the list I was given. I have also kept working on the collection while this expensive lesson unfolds.",
        [30] = "There is a heap of perfectly good machinery on the floor and none of it currently does anything. I will return to paper and metal for a while. At least I understand how to file a tag without somebody asking me to update it first.",
        [31] = "I used to arrange the tags by faction, level and condition. Last night I read a name aloud to check a spelling and found myself wondering who would recognise it. Choose three without regard to rank. I want to begin recording the people, not merely the specimens.",
        [32] = "My old records contained numbers that looked precise and told me almost nothing about their owners. On paper I can leave room beside each name for a detail or a question. I will copy what I can recover, then start doing the rest differently.",
        [33] = "Several names now have small notes beside them. One had repaired a clasp with wire. Another had polished the front so often the back looked neglected. None of this fits neatly into a table, which may be why I keep noticing it.",
        [34] = "The writing has filled an entire wall, and the desk lamp has become an enemy. I have been offered a key to a room with better light, but I suspect that would create more problems than it solves. The glasses seem a more reasonable experiment.",
        [35] = "The locals carry evidence of ordinary days in their pockets: a habit, a meal, something saved for later. I have spent years overlooking it because none of it had a stamped number. Add a few of those things to this week's records so I can see what else I missed.",
        [36] = "A gold pistol is conspicuous enough to be foolish, yet someone carried one anyway. That choice interests me more than its price. If the stories about its owner are true, the object may say more about him than any formal record would.",
        [37] = "I have started writing down the food and small comforts people kept close. It feels intrusive in a different way from reading a tag, but perhaps more honest. These were things they expected to need tomorrow. I would like to preserve that fact alongside their names.",
        [38] = "I found another case tucked behind your bag when you came in. Do not look offended; I recognised the sound of the clasps. Bring twenty from the pile. We can at least make sure those names receive a place in the catalogue.",
        [39] = "When I asked for twenty, I watched the objects disappear into a count. One person at a time may be slower, but it prevents that particular mistake. I have set aside two rifles, and yes, I made some adjustments despite everything I said about the hobby.",
        [40] = "There are pages in the ledger with figures carried over and no explanation of what the figures meant. I am revisiting them while I still can. Five records will close one of the gaps; the rest of the work is mine to do.",
        [41] = "The columns used to balance if I kept working at them. This one will not, though I have checked it three times. The drink is simply to keep me at the desk a little longer. I would rather finish a useful page than spend the evening explaining why I am tired.",
        [42] = "I have moved the chair closer to the window. The light is better there, and the collection looks different from a little distance. Bring the small things on the list. I would like to put the figures aside for an hour and talk when you return.",
        [43] = "I have spent years writing down people I never met. It seems an odd omission that I have no proper record of the one person who kept coming back. The paper is for a new page. The tag will help me finish an older one. After that, there are a few final instructions to leave on the desk. I want you to be able to find things when I am no longer here to point them out.",
        [44] = "The collection is arranged. I've made the decisions I kept putting off and written down where everything should go. There is a chair opposite mine that has ceased to be merely a place for visitors to wait. Bring two bottles, and we can leave the ledger closed. I would like our last conversation to begin with something other than a number.",
        [45] = "There is a page on the desk explaining which things belong to you. The money and equipment are simple enough. The case is empty deliberately. What you put in it is your choice, as is what you remember of the people whose names passed through this room. I wrote this before our last visit. I hoped to finish everything while I could still speak to you, but I kept one final thought for this page.",
    };

    private void AddQuestLocales(QuestSpec spec, string id)
    {
        foreach (var (_, locale) in localeTable.Global)
        {
            locale.AddTransformer(data =>
            {
                ArgumentNullException.ThrowIfNull(data);
                data[$"{id} name"] = spec.Title;
                data[$"{id} description"] = QuestBriefingDetails.TryGetValue(spec.Number, out var detail)
                    ? $"{spec.Intro}\n\n{detail}" : spec.Intro;
                data[$"{id} note"] = string.Empty;
                data[$"{id} failMessageText"] = string.Empty;
                data[$"{id} startedMessageText"] = QuestStartedMessages.TryGetValue(spec.Number, out var started) ? started : "Understood.";
                data[$"{id} successMessageText"] = spec.Success;
                data[$"{id} acceptPlayerMessage"] = "I'll see what I can do.";
                data[$"{id} declinePlayerMessage"] = string.Empty;
                data[$"{id} completePlayerMessage"] = "Here.";
                for (var i = 0; i < spec.Objectives.Count; i++)
                {
                    var o = spec.Objectives[i];
                    data[ConditionId(spec.Number, i + 1)] = ObjectiveText(o);
                }
                return data;
            });
        }
    }

    private static string ObjectiveText(ObjectiveSpec o)
    {
        if (o.Kind == "item")
        {
            var name = ItemAliases.TryGetValue(o.Key, out var a) ? a[0] : o.Key;
            return $"Hand over {o.Count} × {name}";
        }
        var faction = o.Key switch { "bear" => "BEAR ", "usec" => "USEC ", _ => string.Empty };
        var level = o.Level > 0 ? $" level {o.Level}+" : string.Empty;
        return $"Hand over {o.Count} × {faction}PMC dogtag{(o.Count == 1 ? "" : "s")}{level}";
    }

    private void AddTraderLocales()
    {
        foreach (var (_, locale) in localeTable.Global)
        {
            locale.AddTransformer(data =>
            {
                ArgumentNullException.ThrowIfNull(data);
                data[$"{LedgerTraderId} FullName"] = "Ledger";
                data[$"{LedgerTraderId} FirstName"] = "Ledger";
                data[$"{LedgerTraderId} Nickname"] = "Ledger";
                data[$"{LedgerTraderId} Location"] = "Old Industrial Quarter";
                data[$"{LedgerTraderId} Description"] = "A civilian collector, archivist and procurer with an uncomfortable fascination for Tarkov's mercenaries and the things they leave behind. Ledger never served himself, though not for lack of interest. He has spent years cataloguing units, names, equipment and rumours, and seems particularly fascinated by dogtags, their provenance, condition and occasionally even their smell. He maintains a small but unusual network of contacts and is willing to share its benefits with anyone who can bring him material worthy of his collection.";
                return data;
            });
        }
    }

    private static string StableId(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }
    private static string QuestId(int q) => StableId($"ledger-quest-{q:00}");
    private static string ConditionId(int q, int i) => StableId($"ledger-condition-{q:00}-{i:00}");
    private static string RewardId(int q, int i) => StableId($"ledger-reward-{q:00}-{i:00}");
    private static string InstanceId(int q, int i) => StableId($"ledger-item-{q:00}-{i:00}");
}
