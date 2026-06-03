using Candide;
using Candide.Database;
using Candide.CandideUI;
using Candide.CandideUI.Components;
using Candide.CandideUI.Components.Buttons;
using Candide.CandideUI.Components.Holder;
using Candide.CandideUI.Containers;
using Candide.CandideUI.UserControls;
using Candide.CandideUI.WorldOverview;
using Candide.Database.Doodad;
using Candide.GameModels;
using Candide.GameModels.Controllers;
using Candide.GameModels.Helpers;
using Candide.GameModels.Managers;
using Candide.GameModels.Models.Constructions;
using Candide.GameModels.Models.Items;
using Candide.GameModels.Services;
using Candide.GameModels.Systems;
using Candide.GameModels.Systems.Weather;
using Candide.Input;
using Candide.Sound;
using Candide.World;
using Candide.Multiplayer.Network;
using CandideCreator.Shared.Graphics;
using CandideServer;
using CandideServer.Entities;
using CandideServer.MessageModels.Inventories;
using CandideServer.MessageModels.Constructions;
using CandideServer.MessageModels.Entities;
using CandideServer.MessageModels.Skills;
using CandideServer.MessageModels.Weather;
using CandideServer.Models.Auras;
using CandideServer.Models.Citizen;
using CandideServer.Models.Items;
using CandideServer.Models.Quests.EventModels;
using CandideServer.ServerControllers;
using CandideServer.ServerManagers;
using CandideServer.ServerSystems;
using BepInEx.Configuration;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using RomStar.BepInEx.Features;
using RomStar.BepInEx.Input;
using RomStar.BepInEx.Runtime;
using RomStar.BepInEx.UI;
using Shared;
using Shared.Aura;
using Shared.Aura.Args;
using Shared.Data;
using Shared.Data.Furniture;
using Shared.Data.DataModels;
using Shared.Entity;
using Shared.Inventory;
using Shared.Inventory.DeltaModels;
using Shared.Inventory.Models;
using Shared.Models.Auras;
using Shared.Models.Boss;
using Shared.Models.Construction;
using Shared.Models.Items;
using Shared.Models.Player;
using Shared.Models.Skill;
using Shared.Models.Stats;
using Shared.Messages;
using Shared.Text;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Numerics;
using GameIcon = CandideCreator.Shared.Graphics.Icon;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace RomStar.BepInEx.Trainer;

internal static class TrainerWindow
{
    private const float FormLabelWidth = 168f;
    private const float SearchInputWidth = 300f;
    private const float ComboWidth = 340f;
    private const float SmallNumberWidth = 220f;
    private const float ActionButtonWidth = 210f;
    private const float SmallActionButtonWidth = 92f;
    private const int MaxCommandAmount = 99999999;
    private const int MaxVisibleCatalogItems = 300;
    private const string DisplayVersion = "v0.2.66";
    private const int ExtendedBackpackSlots = 72;
    private const int ExtendedBackpackColumns = 6;
    private const string ExtendedBackpackNamePrefix = "RomStar_ExtendedBackpack_";
    private static readonly Vector4 LoadingTextColor = new(1f, 0.24f, 0.18f, 1f);

    private enum TrainerPage
    {
        Basic,
        BuildUnits,
        Items,
        Spawner,
        Player,
        Character,
        Skills,
        Affixes,
        Npc,
        WorldSystem
    }

    private enum UiLanguage
    {
        Cn,
        English
    }

    private enum HotkeyCaptureTarget
    {
        None,
        Trainer,
        Debug,
        Control
    }

    private enum PlayerStatsTab
    {
        Damage,
        General,
        Resistances
    }

    private readonly record struct EquipmentItemRef(ItemInstanceModel Item, string Source, int Slot);
    private readonly record struct PlayerStatEditorDef(string Id, string ChineseName, string EnglishName, int Min, int Max);
    private readonly record struct CitizenPersistentOverrideKey(Guid CitizenId, string Id);
    private sealed record CitizenPersistentOverrideValue(string Key, float Value);
    private sealed record CitizenPersistentOverrideState(
        List<CitizenPersistentOverrideValue> Basic,
        List<CitizenPersistentOverrideValue> Job,
        List<CitizenPersistentOverrideValue> Stat);
    private readonly record struct ItemAuraStatRef(int AuraIndex, string AuraId, string StatId, ItemAuraStatValueKind ValueKind, float InternalValue)
    {
        public float ToDisplayValue()
        {
            return ValueKind == ItemAuraStatValueKind.Additive ? InternalValue : InternalValue * 100f;
        }

        public float FromDisplayValue(float displayValue)
        {
            return ValueKind == ItemAuraStatValueKind.Additive ? displayValue : displayValue / 100f;
        }
    }

    private readonly record struct IdDropdownEntry(string Id, string DisplayName, string SearchText, string? IconId);

    private enum ItemAuraStatValueKind
    {
        Additive,
        AdditiveMultiplier,
        BaseMultiplier,
        BonusMultiplier,
        Multiplier
    }

    private readonly record struct CatalogItem(string Id, string ChineseName, string EnglishName, string? IconId, string CategoryId)
    {
        public string Display => string.IsNullOrWhiteSpace(EnglishName)
            ? $"{CleanInternalId(Id)} [{Id}]"
            : $"{EnglishName} [{Id}]";
    }

    private static string status = "RomStar BepInEx 修改器已加载。";
    private static HotkeyCaptureTarget hotkeyCaptureTarget;
    private static readonly List<CatalogItem> catalogItems = new();
    private static readonly List<CatalogItem> filteredItems = new();
    private static readonly List<CatalogItem> filteredGeneratorItems = new();
    private static readonly Dictionary<Texture2D, nint> iconTextureIds = new();
    private static readonly Dictionary<string, string> trainerChineseGameStrings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<IdDropdownEntry>> idDropdownCache = new(StringComparer.Ordinal);
    private static readonly Queue<Func<bool>> staticDropdownPrewarmQueue = new();
    private static bool staticDropdownPrewarmCompleted;
    private static UiLanguage staticDropdownPrewarmLanguage = UiLanguage.Cn;
    private static readonly Dictionary<string, string> entityNameOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["activated_spike_trap"] = "已激活尖刺陷阱",
        ["aloe"] = "芦荟",
        ["aloe_plant"] = "芦荟",
        ["altar_base"] = "祭坛底座",
        ["altar_broken_crypt"] = "破损墓穴祭坛",
        ["altar_broken_foundry"] = "破损铸造厂祭坛",
        ["altar_crypt"] = "墓穴祭坛",
        ["altar_foundry"] = "铸造厂祭坛",
        ["animal_rug"] = "动物地毯",
        ["animal_trophy"] = "动物战利品",
        ["anvil"] = "铁砧",
        ["apple_tree"] = "苹果树",
        ["apricot_tree"] = "杏树",
        ["basalt_rock_5"] = "玄武岩岩石5",
        ["basalt_rock_6"] = "玄武岩岩石6",
        ["basalt_rock_lava"] = "玄武岩熔岩",
        ["bayleaf_poi"] = "月桂地点",
        ["bear_cub"] = "幼熊",
        ["beehive"] = "蜂箱",
        ["bending_vines"] = "弯曲藤蔓",
        ["cart"] = "手推车",
        ["catapult"] = "投石机",
        ["chest"] = "箱子",
        ["coal_trap"] = "煤炭陷阱",
        ["grave"] = "坟墓",
        ["ladder"] = "梯子",
        ["spike_trap"] = "尖刺陷阱"
    };
    private static readonly Dictionary<string, string> entityWordNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["activated"] = "已激活",
        ["aloe"] = "芦荟",
        ["altar"] = "祭坛",
        ["animal"] = "动物",
        ["apple"] = "苹果",
        ["apricot"] = "杏",
        ["arena"] = "竞技场",
        ["base"] = "底座",
        ["basalt"] = "玄武岩",
        ["bayleaf"] = "月桂",
        ["bear"] = "熊",
        ["bee"] = "蜂",
        ["beehive"] = "蜂箱",
        ["bending"] = "弯曲",
        ["berry"] = "浆果",
        ["broken"] = "破损",
        ["bush"] = "灌木",
        ["cart"] = "手推车",
        ["catapult"] = "投石机",
        ["chest"] = "箱子",
        ["coal"] = "煤炭",
        ["controller"] = "控制器",
        ["crypt"] = "墓穴",
        ["cub"] = "幼崽",
        ["desert"] = "沙漠",
        ["door"] = "门",
        ["dummy"] = "假人",
        ["foundry"] = "铸造厂",
        ["gate"] = "大门",
        ["grave"] = "坟墓",
        ["hidden"] = "隐藏",
        ["iron"] = "铁",
        ["ladder"] = "梯子",
        ["lava"] = "熔岩",
        ["marble"] = "大理石",
        ["mill"] = "磨坊",
        ["mushroom"] = "蘑菇",
        ["plant"] = "植物",
        ["platform"] = "平台",
        ["poi"] = "地点",
        ["rock"] = "岩石",
        ["rug"] = "地毯",
        ["spawner"] = "生成器",
        ["spike"] = "尖刺",
        ["stone"] = "石头",
        ["thrower"] = "投掷器",
        ["trap"] = "陷阱",
        ["tree"] = "树",
        ["treasure"] = "宝藏",
        ["trophy"] = "战利品",
        ["turret"] = "炮塔",
        ["vent"] = "喷口",
        ["vines"] = "藤蔓",
        ["wood"] = "木"
    };
    private static readonly (string Id, string Cn, string En)[] ItemCategories =
    {
        ("all", "全部", "All"),
        ("food", "食物", "Food"),
        ("material", "材料", "Materials"),
        ("equipment", "装备", "Equipment"),
        ("weapon", "武器/工具", "Weapons/Tools"),
        ("armor", "护甲", "Armor"),
        ("ammunition", "弹药", "Ammunition"),
        ("seed", "种子", "Seeds"),
        ("usable", "可使用", "Usable"),
        ("quest", "任务", "Quest"),
        ("offering", "贡品", "Offerings"),
        ("money", "货币", "Money"),
        ("other", "其它", "Other")
    };
    private static TrainerPage selectedPage = TrainerPage.Basic;
    private static string itemSearch = "";
    private static string generatorSearch = "";
    private static int selectedItemCategory;
    private static int selectedGeneratorCategory;
    private static string inventorySearch = "";
    private static string directItemId = "";
    private static int selectedItem;
    private static int selectedGeneratorItem;
    private static int selectedInventorySlot = -1;
    private static Guid? extendedBackpackInventoryId;
    private static CandideWindow? extendedBackpackButtonOwner;
    private static CandideIconButton? extendedBackpackToggleButton;
    private static ExtendedBackpackWindow? extendedBackpackWindow;
    private static bool extendedBackpackVisible;
    private static CandideWindow? pendingExtendedBackpackButtonOwner;
    private static DateTime pendingExtendedBackpackButtonSince = DateTime.MinValue;
    private static int extendedBackpackUiQueued;
    private static int itemAmount = 1;
    private static int generatorAmount = 1;
    private static bool catalogLoaded;
    private static bool catalogLoading;
    private static bool trainerChineseGameStringsLoaded;
    private static string spawnId = "";
    private static string constructId = "";
    private static string bossId = "";
    private static string raidId = "";
    private static string entitySearch = "";
    private static string constructionSearch = "";
    private static string buildConstructionSearch = "";
    private static string bossSearch = "";
    private static string raidSearch = "";
    private static int selectedEntity;
    private static int selectedConstruction;
    private static int selectedBuildConstruction;
    private static int selectedBoss;
    private static int selectedRaid;
    private static string pendingMouseEntityPlacementId = "";
    private static string pendingMouseEntityPlacementName = "";
    private static bool pendingMouseEntityPlacementReady;
    private static int trainerCloseRequested;
    private static int playerHealth = 99999;
    private static int playerEnergy = 99999;
    private static int playerMaxEnergy = 99999;
    private static int playerEnergyRegeneration = 10;
    private static int playerAttackSpeed = 2;
    private static int speedMultiplier = 1;
    private static readonly Dictionary<string, int> playerStatDraftValues = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> playerPersistentStatOverrides = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> playerPersistentStatOriginals = new(StringComparer.Ordinal);
    private static bool keepPlayerHealth;
    private static bool keepPlayerEnergy;
    private static bool keepPlayerMaxEnergy;
    private static bool keepPlayerEnergyRegeneration;
    private static bool keepPlayerAttackSpeed;
    private static bool keepPlayerMoveSpeed;
    private static Guid? sustainedPlayerEntityId;
    private static float? originalPlayerHealth;
    private static float? originalPlayerMaxHealth;
    private static float? originalPlayerHealthBase;
    private static float? originalPlayerEnergy;
    private static float? originalPlayerEnergyBase;
    private static float? originalPlayerEnergyRegeneration;
    private static float? originalPlayerAttackSpeed;
    private static float? originalPlayerAccelerationScale;
    private static DateTime lastPlayerSustainTick = DateTime.MinValue;
    private static bool instantConstruction;
    private static bool ignorePlacingRequirements;
    private static bool noConstructionMaterials;
    private static bool citizenInvincible;
    private static bool citizenNoHunger;
    private static bool citizenHappy;
    private static bool citizenLoyal;
    private static int spawnRateMultiplier = 1;
    private static int cropGrowthMultiplier = 1;
    private static int jobSpeedMultiplier = 1;
    private static int jobProgressMultiplier = 1;
    private static string skillSearch = "";
    private static int selectedSkill;
    private static int skillLevelSet = 10;
    private static int skillExperienceAmount = 500;
    private static int skillExperienceMultiplier = 1;
    private static string expandedSkillId = "";
    private static bool showSkillAdvanced;
    private static bool openSkillEditorPopup;
    private static int favourPoints = 10;
    private static int worshipPoints = 1000;
    private static bool favourPointsInitialized;
    private static string citizenSearch = "";
    private static int selectedCitizen;
    private static string citizenAuraSearch = "";
    private static int selectedCitizenAura;
    private static bool citizenPersistentOverridesLoaded;
    private static readonly Dictionary<string, float> citizenPersistentBasicOverrides = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> citizenPersistentJobOverrides = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> citizenPersistentStatOverrides = new(StringComparer.Ordinal);
    private static int gameTimeScale = 1;
    private static int dayNightScale = 1;
    private static int hourToSet = 8;
    private static string weatherSearch = "";
    private static int selectedWeather;
    private static int positionSlot = 1;
    private static float manualPositionX;
    private static float manualPositionY;
    private static readonly Dictionary<int, Microsoft.Xna.Framework.Vector2> savedPositions = new();
    private static bool mapTeleportEnabled = true;
    private static bool mapTeleportRightWasDown;
    private static Microsoft.Xna.Framework.Vector2? recordedMapTeleportTile;
    private static DateTime recordedMapTeleportTime;
    private static int equipmentSource;
    private static string equipmentSearch = "";
    private static string itemAuraSearch = "";
    private static readonly Dictionary<string, float> itemAuraStatOverrides = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> itemAuraStatDraftValues = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> itemAuraTemplateStatOverrides = new(StringComparer.Ordinal);
    private static readonly HashSet<string> patchedItemAuraTemplates = new(StringComparer.Ordinal);
    private static int selectedEquipmentItem;
    private static int selectedItemAura;
    private static UiLanguage currentLanguage = UiLanguage.Cn;
    private static readonly Dictionary<string, float> baseSkillExperienceGainFactors = new(StringComparer.Ordinal);
    private static readonly FieldInfo? characterSkillDataField = typeof(CharacterSkill).GetField("_skillData", BindingFlags.Instance | BindingFlags.NonPublic);
    private static DateTime lastExtendedBackpackLootTick = DateTime.MinValue;
    private static readonly PlayerStatEditorDef[] PlayerDamageStats =
    {
        new("MeleeDamage", "近战伤害", "Melee Damage", 0, 999999),
        new("RangedDamage", "远程伤害", "Ranged Damage", 0, 999999),
        new("MagicDamage", "魔法伤害", "Magic Damage", 0, 999999),
        new("MeleeDamageModifier", "近战伤害倍率", "Melee Damage Modifier", 0, 999999),
        new("RangedDamageModifier", "远程伤害倍率", "Ranged Damage Modifier", 0, 999999),
        new("MagicDamageModifier", "魔法伤害倍率", "Magic Damage Modifier", 0, 999999),
        new("ThrowingDamageModifier", "投掷伤害倍率", "Throwing Damage Modifier", 0, 999999),
        new("SlashingDamageModifier", "挥砍伤害倍率", "Slashing Damage Modifier", 0, 999999),
        new("BludgeoningDamageModifier", "钝击伤害倍率", "Bludgeoning Damage Modifier", 0, 999999),
        new("PiercingDamageModifier", "穿刺伤害倍率", "Piercing Damage Modifier", 0, 999999),
        new("PyroDamageModifier", "火焰伤害倍率", "Pyro Damage Modifier", 0, 999999),
        new("ChloroDamageModifier", "自然伤害倍率", "Chloro Damage Modifier", 0, 999999),
        new("AquaDamageModifier", "水系伤害倍率", "Aqua Damage Modifier", 0, 999999),
        new("CosmoDamageModifier", "星界伤害倍率", "Cosmo Damage Modifier", 0, 999999),
        new("NecroDamageModifier", "死灵伤害倍率", "Necro Damage Modifier", 0, 999999)
    };
    private static readonly PlayerStatEditorDef[] PlayerGeneralStats =
    {
        new("Health", "最大生命", "Max Health", 1, 999999),
        new("Energy", "最大能量", "Max Energy", 1, 999999),
        new("EnergyRegeneration", "能量恢复", "Energy Regen", 0, 999999),
        new("Armor", "护甲", "Armor", 0, 999999),
        new("MagicResistance", "魔法抗性", "Magic Resistance", 0, 999999),
        new("KnockbackResistance", "击退抗性", "Knockback Resistance", 0, 999999),
        new("AxePower", "斧力", "Axe Power", 0, 999999),
        new("PickaxePower", "镐力", "Pickaxe Power", 0, 999999),
        new("CritChance", "暴击率", "Crit Chance", 0, 100),
        new("CritDamage", "暴击伤害", "Crit Damage", 0, 999999),
        new("MovementSpeed", "移动速度", "Movement Speed", 0, 999999),
        new("AttackSpeed", "攻击速度", "Attack Speed", 1, 999999),
        new("AttackRangeModifier", "攻击范围倍率", "Attack Range Modifier", 0, 999999),
        new("Knockback", "击退", "Knockback", 0, 999999),
        new("LightSource", "光源", "Light Source", 0, 999999)
    };
    private static readonly PlayerStatEditorDef[] PlayerResistanceStats =
    {
        new("status:bleed", "流血抗性", "Bleed Resistance", 0, 100),
        new("status:burning", "燃烧抗性", "Burning Resistance", 0, 100),
        new("status:charm", "魅惑抗性", "Charm Resistance", 0, 100),
        new("status:confusion", "混乱抗性", "Confusion Resistance", 0, 100),
        new("status:disease", "疾病抗性", "Disease Resistance", 0, 100),
        new("status:curse", "诅咒抗性", "Curse Resistance", 0, 100),
        new("status:petrified", "石化抗性", "Petrified Resistance", 0, 100),
        new("status:poisoned", "中毒抗性", "Poison Resistance", 0, 100),
        new("status:root", "缠绕抗性", "Root Resistance", 0, 100),
        new("status:slow", "减速抗性", "Slow Resistance", 0, 100),
        new("status:stun", "眩晕抗性", "Stun Resistance", 0, 100),
        new("SlashingResistance", "挥砍抗性", "Slashing Resistance", 0, 100),
        new("BludgeoningResistance", "钝击抗性", "Bludgeoning Resistance", 0, 100),
        new("PiercingResistance", "穿刺抗性", "Piercing Resistance", 0, 100),
        new("PyroResistance", "火焰抗性", "Pyro Resistance", 0, 100),
        new("ChloroResistance", "自然抗性", "Chloro Resistance", 0, 100),
        new("AquaResistance", "水系抗性", "Aqua Resistance", 0, 100),
        new("CosmoResistance", "星界抗性", "Cosmo Resistance", 0, 100),
        new("NecroResistance", "死灵抗性", "Necro Resistance", 0, 100)
    };

    public static void Configure(ConfigFile config)
    {
        currentLanguage = UiLanguage.English;
        status = "RomStar Nexus build loaded.";
    }

    private static UiLanguage ParseLanguage(string? value)
    {
        return string.Equals(value, "English", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.English
            : UiLanguage.Cn;
    }

    private static string LanguageConfigValue(UiLanguage language)
    {
        return language == UiLanguage.English ? "English" : "CN";
    }

    private static string T(string cn, string en)
    {
        return en;
    }

    private static string TranslateGameText(StringId textId)
    {
        return textId.GetTranslation();
    }

    private static bool TryGetTrainerChineseGameString(string? key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        EnsureTrainerChineseGameStringsLoaded();
        if (!string.IsNullOrEmpty(key) && trainerChineseGameStrings.TryGetValue(key, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private static bool IsUsableTranslatedName(string name, string id)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            name != id &&
            !name.StartsWith("<", StringComparison.Ordinal) &&
            !name.EndsWith(">", StringComparison.Ordinal);
    }

    private static void EnsureTrainerChineseGameStringsLoaded()
    {
        if (trainerChineseGameStringsLoaded)
        {
            return;
        }

        trainerChineseGameStringsLoaded = true;
        string? contentRoot = Globals.ContentPath;
        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            contentRoot = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "..", "Content"));
        }

        string localePath = Path.Combine(contentRoot, "localization", "locale_zh_CN");
        if (!File.Exists(localePath))
        {
            localePath = @"C:\SteamLibrary\steamapps\common\romestead\Content\localization\locale_zh_CN";
        }

        if (!File.Exists(localePath))
        {
            return;
        }

        try
        {
            using FileStream input = File.OpenRead(localePath);
            using BinaryReader reader = new(input);
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = reader.ReadString();
                string text = reader.ReadString();
                trainerChineseGameStrings[key] = text;
            }
        }
        catch
        {
            trainerChineseGameStrings.Clear();
        }
    }

    public static void TickAlways()
    {
        try
        {
            HandleMapTeleportInput();
            QueueExtendedBackpackUiUpdate();
            PrewarmCatalogCache();
            PrewarmStaticDropdownCaches();
            HandlePendingMouseEntityPlacement();
            ApplyCitizenPersistentOverrides();

            DateTime utcNow = DateTime.UtcNow;
            if ((utcNow - lastExtendedBackpackLootTick).TotalMilliseconds >= 120)
            {
                lastExtendedBackpackLootTick = utcNow;
                TryLootOverflowItemsIntoExtendedBackpack();
            }

            if ((utcNow - lastPlayerSustainTick).TotalMilliseconds < 250)
            {
                return;
            }

            lastPlayerSustainTick = utcNow;
            if (AnyPlayerSustainEnabled())
            {
                ApplySustainedPlayerModifiers();
            }

            ApplyPlayerPersistentStatOverrides();
            ApplyItemAuraStatOverrides();
            ApplySafeInstantConstruction();
        }
        catch (Exception ex)
        {
            status = T("保持玩家属性失败：", "Failed to keep player stats: ") + ex.Message;
        }
    }

    public static bool ConsumeTrainerCloseRequest()
    {
        return Interlocked.Exchange(ref trainerCloseRequested, 0) != 0;
    }

    public static void Draw()
    {
        ApplyCitizenUnitToggles();
        DrawHeader();
        ImGui.Separator();

        ImGui.BeginChild("RomStarNav", new Vector2(170f, 0f), true);
        DrawNavButton(TrainerPage.Basic, T("基础", "Basic"));
        DrawNavButton(TrainerPage.Items, T("物品", "Items"));
        DrawNavButton(TrainerPage.Spawner, T("生成器", "Spawner"));
        DrawNavButton(TrainerPage.Player, T("玩家", "Player"));
        DrawNavButton(TrainerPage.BuildUnits, T("建造/单位", "Build/Units"));
        DrawNavButton(TrainerPage.Character, T("角色", "Character"));
        DrawNavButton(TrainerPage.Skills, T("技能", "Skills"));
        DrawNavButton(TrainerPage.Affixes, T("装备词条", "Affixes"));
        DrawNavButton(TrainerPage.Npc, "NPC");
        DrawNavButton(TrainerPage.WorldSystem, T("世界/系统", "World/System"));
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("RomStarPage", Vector2.Zero, true);
        DrawSelectedPage();
        ImGui.EndChild();
    }

    private static void DrawNavButton(TrainerPage page, string label)
    {
        bool active = selectedPage == page;
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.45f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.94f, 0.72f, 1f));
        }

        if (ImGui.Button(label, new Vector2(-1f, 34f)))
        {
            selectedPage = page;
        }

        if (active)
        {
            ImGui.PopStyleColor(2);
        }
    }

    private static void DrawSelectedPage()
    {
        switch (selectedPage)
        {
            case TrainerPage.Basic:
                DrawBasicTab();
                break;
            case TrainerPage.BuildUnits:
                DrawBuildUnitsTab();
                break;
            case TrainerPage.Items:
                DrawItemsTab();
                break;
            case TrainerPage.Spawner:
                DrawSpawnerTab();
                break;
            case TrainerPage.Player:
                DrawPlayerTab();
                break;
            case TrainerPage.Skills:
                DrawSkillsTab();
                break;
            case TrainerPage.Character:
                DrawCharacterTab();
                break;
            case TrainerPage.Affixes:
                DrawAffixesTab();
                break;
            case TrainerPage.Npc:
                DrawNpcTab();
                break;
            case TrainerPage.WorldSystem:
                DrawWorldSystemTab();
                break;
        }
    }

    private static void DrawHeader()
    {
        ImGui.BeginChild("RomStarAnnouncement", new Vector2(0f, 58f), true);
        ImGui.TextColored(new Vector4(0.96f, 0.88f, 0.64f, 1f), "ROMSTAR");
        ImGui.EndChild();
    }

    private static void ClearIdDropdownCache()
    {
        idDropdownCache.Clear();
        staticDropdownPrewarmQueue.Clear();
        staticDropdownPrewarmCompleted = false;
        staticDropdownPrewarmLanguage = currentLanguage;
    }

    private static void DrawBasicTab()
    {
        DrawPageTitle(T("基础", "Basic"));
        ImGui.TextWrapped(T(
            "常用保存、热键和状态功能。",
            "Common save, hotkey, and status tools."));
        ImGui.Separator();

        if (ImGui.Button(T("保存游戏", "Save Game")))
        {
            status = RunCommand("save_game");
        }
        ImGui.SameLine();
        if (ImGui.Button(T("保存角色", "Save Character")))
        {
            status = RunCommand("save_character");
        }

        ImGui.Separator();
        ImGui.TextWrapped(T(
            "当前背包和扩展背包工具。",
            "Current backpack and extra backpack tools."));

        ImGui.Separator();
        DrawToolSectionHeader(T("热键", "Hotkeys"));
        DrawHotkeyCaptureRow(T("修改器呼出键", "Trainer Hotkey"), HotkeyCaptureTarget.Trainer, HotkeyConfig.TrainerBinding, HotkeyConfig.SetTrainerHotkey);
        CaptureHotkeyIfNeeded();
        ImGui.TextDisabled(T(
            "默认 F1 打开/关闭修改器。点击设置后按下新按键，可按住 Ctrl / Alt / Shift 设置组合键。",
            "F1 opens/closes the trainer by default. Click Set and press a new key; hold Ctrl / Alt / Shift for combinations."));
    }

    private static void DrawHotkeyCaptureRow(string label, HotkeyCaptureTarget target, HotkeyBinding binding, Action<HotkeyBinding> apply)
    {
        DrawFormLabel(label);
        ImGui.Text(HotkeyConfig.HotkeyName(binding));
        ImGui.SameLine();
        string buttonText = hotkeyCaptureTarget == target
            ? T("请按键...", "Press key...")
            : T("设置", "Set");
        if (ImGui.Button(buttonText + "##hotkey-" + target, new Vector2(132f, 30f)))
        {
            hotkeyCaptureTarget = target;
            status = T($"请按下新的 {label} 热键。可按住 Ctrl/Alt/Shift 作为组合键。", $"Press the new {label} hotkey. Hold Ctrl/Alt/Shift for combinations.");
        }

        if (hotkeyCaptureTarget == target && NativeInput.TryGetPressedKeyboardKey(out int virtualKey))
        {
            HotkeyBinding newBinding = new(virtualKey, NativeInput.CurrentModifiers());
            apply(newBinding);
            hotkeyCaptureTarget = HotkeyCaptureTarget.None;
            status = T($"{label} 已设置为 {HotkeyConfig.HotkeyName(newBinding)}。", $"{label} set to {HotkeyConfig.HotkeyName(newBinding)}.");
        }
    }

    private static void CaptureHotkeyIfNeeded()
    {
        if (hotkeyCaptureTarget == HotkeyCaptureTarget.None || !NativeInput.TryGetPressedKeyboardKey(out int virtualKey))
        {
            return;
        }

        HotkeyBinding binding = new(virtualKey, NativeInput.CurrentModifiers());
        switch (hotkeyCaptureTarget)
        {
            case HotkeyCaptureTarget.Trainer:
                HotkeyConfig.SetTrainerHotkey(binding);
                status = T($"修改器窗口已设置为 {HotkeyConfig.HotkeyName(binding)}。", $"Trainer Window set to {HotkeyConfig.HotkeyName(binding)}.");
                break;
            case HotkeyCaptureTarget.Debug:
                HotkeyConfig.SetDebugHotkey(binding);
                status = T($"中文调试窗口已设置为 {HotkeyConfig.HotkeyName(binding)}。", $"Chinese Debug Window set to {HotkeyConfig.HotkeyName(binding)}.");
                break;
            case HotkeyCaptureTarget.Control:
                HotkeyConfig.SetControlHotkey(binding);
                status = T($"控制台窗口已设置为 {HotkeyConfig.HotkeyName(binding)}。", $"Control Console set to {HotkeyConfig.HotkeyName(binding)}.");
                break;
        }

        hotkeyCaptureTarget = HotkeyCaptureTarget.None;
    }

    private static void DrawBuildUnitsTab()
    {
        DrawPageTitle(T("建造/单位", "Build/Units"));
        ImGui.TextWrapped(T(
            "建造、市民/单位和生产倍率工具。",
            "Building, citizens/units, and production multiplier tools."));
        ImGui.Separator();

        DrawToolSectionHeader(T("建造", "Building"));
        DrawBuildControls();

        ImGui.Separator();
        DrawToolSectionHeader(T("市民/单位", "Citizens/Units"));
        DrawCitizenUnitControls();

        ImGui.Separator();
        DrawToolSectionHeader(T("生产倍率", "Production Multipliers"));
        DrawProductionMultiplierControls();
    }

    private static void DrawBuildControls()
    {
        bool noMaterials = noConstructionMaterials;
        if (DrawCheckboxRow(T("忽略建造材料", "Ignore Materials"), ref noMaterials))
        {
            SetNoConstructionMaterials(noMaterials);
        }

        bool instant = instantConstruction;
        if (DrawCheckboxRow(T("瞬间建造", "Instant Build"), ref instant))
        {
            instantConstruction = instant;
            Cheats.InstantActions = false;
            status = instant ? T("已开启安全瞬间建造，不会影响吃食物。", "Safe instant build enabled; food use is not affected.") : T("已关闭瞬间建造。", "Instant build disabled.");
        }
        ImGui.TextDisabled(T("已停用原版全局瞬间动作，避免快捷键吃食物时被瞬间吃光。", "Vanilla global instant actions are disabled to avoid consuming food instantly from hotkeys."));

        bool ignorePlacement = ignorePlacingRequirements;
        if (DrawCheckboxRow(T("忽略放置要求", "Ignore Placement Rules"), ref ignorePlacement))
        {
            ignorePlacingRequirements = ignorePlacement;
            Cheats.IgnorePlacingRequirements = ignorePlacement;
            status = ignorePlacement ? T("已开启忽略放置要求。", "Ignore placement rules enabled.") : T("已关闭忽略放置要求。", "Ignore placement rules disabled.");
        }

        DrawIdDropdownCommand(
            T("搜索建筑", "Search Construction"),
            T("建筑列表", "Construction List"),
            T("生成建筑", "Spawn Construction"),
            "build-construction-search",
            ref buildConstructionSearch,
            ref selectedBuildConstruction,
            GetConstructionIds,
            ConstructionDisplayName,
            "construct",
            ConstructionIconId);

        ImGui.SameLine();
        if (ImGui.Button(T("建造全部建筑", "Build All Buildings"), new Vector2(ActionButtonWidth, 32f)))
        {
            status = RunCommand("buildings_construct_all");
        }
    }

    private static void DrawCitizenUnitControls()
    {
        if (ImGui.Button(T("添加普通 NPC", "Add Generic NPC"), new Vector2(ActionButtonWidth, 32f)))
        {
            status = RunCommand("citizen_add_generic");
        }
        ImGui.SameLine();
        if (ImGui.Button(T("刷一波敌人", "Spawn Enemy Wave"), new Vector2(ActionButtonWidth, 32f)))
        {
            status = RunCommand("cheat_spawn_wave");
        }

        DrawCheckboxRow(T("市民无敌", "Citizen Invincible"), ref citizenInvincible);
        DrawCheckboxRow(T("市民免饥饿", "Citizen No Hunger"), ref citizenNoHunger);
        DrawCheckboxRow(T("市民满幸福", "Citizen Max Happiness"), ref citizenHappy);
        DrawCheckboxRow(T("市民满忠诚", "Citizen Max Loyalty"), ref citizenLoyal);
        ImGui.TextDisabled(T(
            "市民状态开关会在打开修改器时持续生效，关闭后停止继续写入。",
            "Citizen state toggles keep applying while the trainer is open; disable them to stop writing values."));
    }

    private static void DrawProductionMultiplierControls()
    {
        DrawIntApplyRow(T("刷怪速率倍率", "Spawn Rate Multiplier"), "spawn-rate", ref spawnRateMultiplier, 0, 100, SetSpawnRateMultiplier);
        DrawIntApplyRow(T("作物生长倍率", "Crop Growth Multiplier"), "crop-growth", ref cropGrowthMultiplier, 0, 100, SetCropGrowthMultiplier);
        DrawIntApplyRow(T("工作速度倍率", "Job Speed Multiplier"), "job-speed", ref jobSpeedMultiplier, 0, 100, SetJobSpeedMultiplier);
        DrawIntApplyRow(T("工作进度倍率", "Job Progress Multiplier"), "job-progress", ref jobProgressMultiplier, 0, 100, SetJobProgressMultiplier);
    }

    private static void DrawItemsTab()
    {
        DrawPageTitle(T("物品", "Items"));
        if (!TryEnsureCatalogLoaded(allowBuild: false))
        {
            DrawAnimatedLoadingText(T("物品目录正在加载，请稍候", "Item catalog is loading. Please wait"));
            ImGui.Separator();
            DrawTextInput(T("物品 ID", "Item ID"), "direct-item-id", ref directItemId, 120u);
            DrawNumberInput(T("数量", "Amount"), "item-amount-loading", ref itemAmount, 1, MaxCommandAmount);
            if (ImGui.Button(T("按 ID 添加", "Add By ID"), new Vector2(ActionButtonWidth, 32f)))
            {
                string id = directItemId.Trim();
                status = string.IsNullOrWhiteSpace(id)
                    ? T("请输入物品 ID。", "Enter an item ID.")
                    : RunCommand($"item {id} {itemAmount}");
            }
            return;
        }

        ImGui.TextWrapped(T($"物品目录：{catalogItems.Count} 个。", $"Item catalog: {catalogItems.Count} entries."));

        if (DrawItemCategorySelector(T("分类", "Category"), "item-category", ref selectedItemCategory))
        {
            ApplyItemFilter();
            selectedItem = 0;
        }

        if (DrawSearchInput(T("搜索物品", "Search Items"), "item-search", ref itemSearch))
        {
            ApplyItemFilter();
            selectedItem = 0;
        }

        DrawNumberInput(T("数量", "Amount"), "item-amount", ref itemAmount, 1, MaxCommandAmount);

        if (filteredItems.Count > 0)
        {
            DrawItemSelector(T("物品列表", "Item List"), "item-list", filteredItems, ref selectedItem, GetCatalogDisplayName);

            if (ImGui.Button(T("生成到背包", "Spawn To Backpack"), new Vector2(ActionButtonWidth, 32f)))
            {
                CatalogItem item = filteredItems[selectedItem];
                status = RunCommand($"item {item.Id} {itemAmount}");
            }
            ImGui.SameLine();
            if (DrawGameButton(T("生成到脚下", "Spawn At Feet"), new Vector2(ActionButtonWidth, 32f)))
            {
                CatalogItem item = filteredItems[selectedItem];
                status = SpawnWorldItem(item.Id, itemAmount);
            }
        }
        else
        {
            ImGui.TextWrapped(T("没有匹配物品。", "No matching items."));
        }

        ImGui.Separator();
        DrawTextInput(T("物品 ID", "Item ID"), "direct-item-id", ref directItemId, 120u);
        if (ImGui.Button(T("按 ID 添加", "Add By ID"), new Vector2(ActionButtonWidth, 32f)))
        {
            string id = directItemId.Trim();
            status = string.IsNullOrWhiteSpace(id)
                ? T("请输入物品 ID。", "Enter an item ID.")
                : RunCommand($"item {id} {itemAmount}");
        }
        ImGui.SameLine();
        if (DrawGameButton(T("按 ID 生成到脚下", "Spawn By ID At Feet"), new Vector2(ActionButtonWidth, 32f)))
        {
            string id = directItemId.Trim();
            status = string.IsNullOrWhiteSpace(id)
                ? T("请输入物品 ID。", "Enter an item ID.")
                : SpawnWorldItem(id, itemAmount);
        }
    }

    private static void DrawInventoryTab()
    {
        DrawPageTitle(T("背包", "Inventory"));
        ImGui.TextWrapped(T(
            "这里包含玩家背包查看、排序、复制 ID，以及手动打开的 72 格拓展背包。",
            "This page contains backpack viewing, sorting, ID copy, and a manually opened 72-slot extra backpack."));
        ImGui.Separator();

        DrawExtendedBackpackSection();
        ImGui.Separator();

        if (!GameState.TryGetLocalPlayerInventory(out SimpleInventory? inventory) || inventory == null)
        {
            ImGui.TextWrapped(T("请先进入存档，背包数据准备好后这里会显示内容。", "Enter a save first; backpack contents will appear here once ready."));
            return;
        }

        int occupiedSlots = inventory.Model.InventorySlots.Count(item => item != null);
        ImGui.TextWrapped(T(
            $"背包：{occupiedSlots}/{inventory.Model.InventorySlots.Length} 格已使用",
            $"Backpack: {occupiedSlots}/{inventory.Model.InventorySlots.Length} slots used"));

        if (DrawSearchInput(T("搜索背包", "Search Inventory"), "inventory-search", ref inventorySearch))
        {
            selectedInventorySlot = -1;
        }

        if (DrawGameButton(T("刷新列表", "Refresh List"), new Vector2(120f, 30f)))
        {
            selectedInventorySlot = -1;
            status = T("已刷新背包列表。", "Inventory list refreshed.");
        }
        ImGui.SameLine();
        if (DrawGameButton(T("排序背包", "Sort Inventory"), new Vector2(120f, 30f)))
        {
            try
            {
                SimpleInventoryManager.SortInventory(inventory.Model.Id);
                selectedInventorySlot = -1;
                status = T("已调用原版背包排序。", "Native inventory sort requested.");
            }
            catch (Exception ex)
            {
                status = T("排序背包失败：", "Failed to sort inventory: ") + ex.Message;
            }
        }

        DrawToolSectionHeader(T("当前背包", "Current Backpack"));
        DrawInventorySlotTable(inventory);
        DrawSelectedInventoryItemDetails(inventory);
    }

    private static void QueueExtendedBackpackUiUpdate()
    {
        if (Interlocked.Exchange(ref extendedBackpackUiQueued, 1) != 0)
        {
            return;
        }

        DebugSystem.Queue((Action)(() =>
        {
            try
            {
                ApplyExtendedBackpackUi();
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref extendedBackpackUiQueued, 0);
            }
        }));
    }

    private static void ApplyExtendedBackpackUi()
    {
        try
        {
            CandideWindow? inventoryWindow = PlayerUi.InventoryWindow;
            if (inventoryWindow == null ||
                !((CandideUiElement)inventoryWindow).Visible ||
                ((CandideUiElement)inventoryWindow).Desktop == null)
            {
                extendedBackpackVisible = false;
                CloseExtendedBackpackWindow();
                extendedBackpackButtonOwner = null;
                extendedBackpackToggleButton = null;
                pendingExtendedBackpackButtonOwner = null;
                pendingExtendedBackpackButtonSince = DateTime.MinValue;
                return;
            }

            if (extendedBackpackButtonOwner != inventoryWindow || extendedBackpackToggleButton == null)
            {
                if (pendingExtendedBackpackButtonOwner != inventoryWindow)
                {
                    pendingExtendedBackpackButtonOwner = inventoryWindow;
                    pendingExtendedBackpackButtonSince = DateTime.UtcNow;
                    return;
                }

                if ((DateTime.UtcNow - pendingExtendedBackpackButtonSince).TotalMilliseconds < 350)
                {
                    return;
                }
            }

            EnsureExtendedBackpackToggleButton(inventoryWindow);
            if (!extendedBackpackVisible)
            {
                CloseExtendedBackpackWindow();
                return;
            }

            SharedInventoryModel? storage = EnsureExtendedBackpackInventory();
            if (storage == null)
            {
                CloseExtendedBackpackWindow();
                return;
            }

            EnsureExtendedBackpackWindow(inventoryWindow, storage.Id);
            PositionExtendedBackpackWindow(inventoryWindow);
        }
        catch
        {
        }
    }

    private static void EnsureExtendedBackpackToggleButton(CandideWindow inventoryWindow)
    {
        if (extendedBackpackButtonOwner == inventoryWindow &&
            extendedBackpackToggleButton != null &&
            extendedBackpackToggleButton.Parent != null)
        {
            extendedBackpackToggleButton.Visible = true;
            extendedBackpackToggleButton.Highlight = extendedBackpackVisible;
            return;
        }

        CandideUiElement? child = ((CandideUiContainerSingleItem)inventoryWindow).Child;
        if (child is not CandidePanel panel)
        {
            return;
        }

        extendedBackpackButtonOwner = inventoryWindow;
        extendedBackpackToggleButton = new CandideIconButton("bag_rmi", IconFlag.Medium)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new CandideThickness(0, 1, 1, 0),
            OnClickAction = ToggleExtendedBackpack
        };
        ((CandideUiContainerMultipleItems)panel).AddChild(extendedBackpackToggleButton, false);
        pendingExtendedBackpackButtonOwner = null;
        pendingExtendedBackpackButtonSince = DateTime.MinValue;
    }

    private static void ToggleExtendedBackpack()
    {
        extendedBackpackVisible = !extendedBackpackVisible;
        if (extendedBackpackToggleButton != null)
        {
            extendedBackpackToggleButton.Highlight = extendedBackpackVisible;
        }

        if (!extendedBackpackVisible)
        {
            CloseExtendedBackpackWindow();
        }
    }

    private static void CloseExtendedBackpackWindow()
    {
        try
        {
            if (extendedBackpackWindow != null)
            {
                if (((CandideUiElement)extendedBackpackWindow).Visible)
                {
                    extendedBackpackWindow.Close();
                }
                else
                {
                    extendedBackpackWindow.Inventory = null;
                }
            }
        }
        catch
        {
        }
    }

    private static void EnsureExtendedBackpackWindow(CandideWindow inventoryWindow, Guid inventoryId)
    {
        if (extendedBackpackWindow == null)
        {
            extendedBackpackWindow = new ExtendedBackpackWindow
            {
                MouseSettingsId = null
            };
        }

        extendedBackpackWindow.InventoryId = inventoryId;
        if (!((CandideUiElement)extendedBackpackWindow).Visible)
        {
            CandideDesktop? desktop = ((CandideUiElement)inventoryWindow).Desktop;
            if (desktop != null)
            {
                extendedBackpackWindow.Show(desktop, null, null);
            }
        }
    }

    private static void PositionExtendedBackpackWindow(CandideWindow inventoryWindow)
    {
        if (extendedBackpackWindow == null)
        {
            return;
        }

        Microsoft.Xna.Framework.Vector2 inventoryPosition = ((CandideUiElement)inventoryWindow).CalculateScreenPosition();
        ((CandideUiElement)extendedBackpackWindow).Left = (int)inventoryPosition.X + ((CandideUiElement)inventoryWindow).Bounds.Width + 5;
        ((CandideUiElement)extendedBackpackWindow).Top = (int)inventoryPosition.Y;
        extendedBackpackWindow.UserHasMovedWindowPosition = false;
    }

    private sealed class ExtendedBackpackWindow : CandideWindow
    {
        private readonly InventoryControl inventoryControl;
        private GetResponseHandler? getInventoryHandle;
        private Guid? inventoryId;

        public SimpleInventory? Inventory
        {
            get => inventoryControl.Inventory;
            set
            {
            if (inventoryControl.Inventory == value)
                {
                    return;
                }

                if (inventoryControl.Inventory != null &&
                    value != null &&
                    inventoryControl.Inventory.Model.Id == value.Model.Id)
                {
                    inventoryControl.Inventory.UpdateFromInventoryModel(value.Model);
                    inventoryControl.UpdateSlots();
                    return;
                }

                inventoryControl.Inventory = value;
            }
        }

        public Guid? InventoryId
        {
            get => inventoryId;
            set
            {
                if (inventoryId == value)
                {
                    return;
                }

                inventoryId = value;
                if (((CandideUiElement)this).Visible)
                {
                    RefreshInventoryBinding();
                }
            }
        }

        public ExtendedBackpackWindow()
        {
            ((CandideUiElement)this).Padding = new CandideThickness(8);
            ((CandideUiElement)this).DragDirection = DragDirection.None;
            inventoryControl = new InventoryControl(false, true)
            {
                ColumnCount = ExtendedBackpackColumns,
                OnMouseRightClickFunc = OnExtendedBackpackRightClick,
                OnMouseSpecialClickFunc = args => CandideExtendedInteractionsHandler.InventoryItemSpecialHandler.SpecialAction(args.Slot)
            };
            CandidePanel panel = new()
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            ((CandideUiContainerMultipleItems)panel).AddChild(inventoryControl, false);
            SetChild(panel);
        }

        protected override void OnWindowShown()
        {
            SimpleInventoryController.InventoriesChanged -= OnInventoryChanged;
            SimpleInventoryController.InventoriesChanged += OnInventoryChanged;
            RefreshInventoryBinding();
            base.OnWindowShown();
        }

        protected override void OnWindowClosed()
        {
            Guid? activeInventoryId = inventoryControl.Inventory?.Model.Id;
            if (activeInventoryId.HasValue && IsDraggingFromInventory(activeInventoryId.Value))
            {
                CandideExtendedInteractionsHandler.ResetDraggedContent();
            }

            if (inventoryId.HasValue)
            {
                SimpleInventoryService.SendStopListeningOnSimpleInventory(new StopListeningOnSimpleInventoryMessage
                {
                    InventoryId = inventoryId.Value
                });
            }

            getInventoryHandle?.Remove();
            getInventoryHandle = null;
            SimpleInventoryController.InventoriesChanged -= OnInventoryChanged;
            inventoryControl.ResetErrorMessage();
            Inventory = null;
            base.OnWindowClosed();
        }

        private void OnInventoryChanged(GameStateActiveInventoriesArgs args)
        {
            SimpleInventory? inventory = inventoryControl.Inventory;
            if (inventory == null || args.InventoryId != inventory.Model.Id)
            {
                return;
            }

            if (args.Inventory == null)
            {
                Close();
                return;
            }

            inventoryControl.UpdateSlots();
        }

        private void RefreshInventoryBinding()
        {
            getInventoryHandle?.Remove();
            getInventoryHandle = null;
            inventoryControl.ResetErrorMessage();
            if (!inventoryId.HasValue)
            {
                Inventory = null;
                return;
            }

            getInventoryHandle = SimpleInventoryService.GetAndListenOnSimpleInventory(new GetSimpleInventoryMessage
            {
                InventoryId = inventoryId.Value
            }, response =>
            {
                Inventory = SimpleInventoryController.SyncRegisterOrUpdateActiveInventory(response.Model);
            }, OnInventoryError, 3f);
        }

        private void OnInventoryError(byte code)
        {
            if (Inventory != null)
            {
                return;
            }

            Inventory = null;
            inventoryControl.DisplayErrorMessage(code == 0
                ? "Time-out fetching inventory data"
                : "Something went wrong fetching the inventory data");
        }

        private static bool OnExtendedBackpackRightClick(InventoryControlOnMouseRightClickArgs args)
        {
            if (args.ItemInstance == null)
            {
                return false;
            }

            try
            {
                if (TryMoveExtendedBackpackItemToPlayerInventory(args.Inventory, args.Index, args.ItemInstance))
                {
                    SoundPlayer.PlayEventOneShot(args.ItemInstance.Data.Flags.HasFlag(ItemFlag.Money)
                        ? "event:/items/coin_pickup"
                        : "event:/interface/holder_drop", 1f, 1f);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryMoveExtendedBackpackItemToPlayerInventory(SimpleInventory sourceInventory, int sourceIndex, ItemInstanceModel item)
        {
            if (!GameState.TryGetLocalPlayerInventory(out SimpleInventory? playerInventory) || playerInventory == null)
            {
                return false;
            }

            return SimpleInventoryManager.AddItemFromSourceAction(sourceInventory, sourceIndex, playerInventory, null, allowPartialStack: false);
        }

        private static bool IsDraggingFromInventory(Guid inventoryId)
        {
            ICandideDraggableHolderContent? draggedContent = CandideExtendedInteractionsHandler.DraggedContent;
            if (draggedContent?.Parent is CandideInventorySlot slot)
            {
                return slot.Inventory.Model.Id == inventoryId;
            }

            return false;
        }
    }

    private static void TryLootOverflowItemsIntoExtendedBackpack()
    {
        try
        {
            if (Globals.Game?.Player == null ||
                ServerGameState.WorldItems.Count == 0 ||
                !GameState.TryGetLocalPlayerInventory(out SimpleInventory? playerInventory) ||
                playerInventory == null)
            {
                return;
            }

            SharedInventoryModel? storage = EnsureExtendedBackpackInventory();
            if (storage == null)
            {
                return;
            }

            Guid playerId = GameState.LocalPlayer.EntityId;
            Microsoft.Xna.Framework.Vector2 playerPosition = Globals.Game.Player.Position2;
            float gameSeconds = (float)ServerGlobals.GameTime.TotalGameTime.TotalSeconds;

            foreach (ServerWorldItemModel serverItem in ServerGameState.WorldItems.Values.ToList())
            {
                WorldItemModel worldItem = serverItem.WorldItemModel;
                if (worldItem.Type != WorldItemModel.WorldItemType.Item ||
                    !worldItem.ItemInstanceId.HasValue ||
                    gameSeconds - serverItem.SpawnTime < 0.75f)
                {
                    continue;
                }

                Microsoft.Xna.Framework.Vector2 itemPosition = new(worldItem.Position.X, worldItem.Position.Y);
                if (Microsoft.Xna.Framework.Vector2.Distance(itemPosition, playerPosition) > 24f ||
                    !ServerGameState.TryGetItemInstance(worldItem.ItemInstanceId.Value, out ItemInstanceModel? item) ||
                    item == null ||
                    InventoryHelper.CanAddItemInstance(playerInventory.Model, item, allowPartialStack: true, out _) ||
                    !InventoryHelper.CanAddItemInstance(storage, item, allowPartialStack: true, out _))
                {
                    continue;
                }

                int lootedAmount = WorldItemServerManager.TryLootWorldItem(serverItem, playerId, storage.Id, null);
                if (lootedAmount > 0)
                {
                    OnPlayerLootItemEvent.Send(worldItem.ItemInstanceId.Value, worldItem.DataId, lootedAmount, playerId);
                    SimpleInventoryController.SyncRegisterOrUpdateActiveInventory(storage);
                }
            }
        }
        catch
        {
        }
    }

    private static void DrawExtendedBackpackSection()
    {
        DrawToolSectionHeader(T("拓展背包", "Extra Backpack"));
        ImGui.TextWrapped(T(
            "72 格额外仓库，使用游戏原生仓库窗口。请进入存档后点击打开。",
            "A 72-slot extra storage using the game's native storage window. Enter a save, then open it here."));
        if (DrawGameButton(T("打开拓展背包", "Open Extra Backpack"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = OpenExtendedBackpackStorage();
        }
        ImGui.SameLine();
        if (DrawGameButton(T("同步拓展背包", "Sync Extra Backpack"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = EnsureExtendedBackpackInventory() != null
                ? T("拓展背包已准备好。", "Extra backpack is ready.")
                : T("拓展背包准备失败。", "Failed to prepare extra backpack.");
        }
    }

    private static string OpenExtendedBackpackStorage()
    {
        try
        {
            SharedInventoryModel? storage = EnsureExtendedBackpackInventory();
            if (storage == null)
            {
                return T("扩展背包准备失败，请先进入单人存档。", "Failed to prepare extended backpack. Enter a single-player save first.");
            }

            SimpleInventoryController.SyncRegisterOrUpdateActiveInventory(storage);
            PlayerUi.ShowItemStorage(storage.Id, null, 0f, ExtendedBackpackColumns, gravestoneUi: false, overrideAlreadyOpened: true, placedByPlayer: false);
            return T("已打开扩展背包。", "Extended backpack opened.");
        }
        catch (Exception ex)
        {
            return T("打开扩展背包失败：", "Failed to open extended backpack: ") + ex.Message;
        }
    }

    private static SharedInventoryModel? EnsureExtendedBackpackInventory()
    {
        try
        {
            if (GameState.LocalPlayer == null)
            {
                return null;
            }

            Guid ownerId = GameState.LocalPlayer.EntityId;
            string name = ExtendedBackpackNamePrefix + ownerId.ToString("N");
            if (extendedBackpackInventoryId.HasValue &&
                ServerGameState.TryGetInventory(extendedBackpackInventoryId.Value, out SharedInventoryModel? cached) &&
                cached != null)
            {
                return EnsureExtendedBackpackSlotCount(cached);
            }

            SharedInventoryModel? existing = ServerGameState.Inventories.Values.FirstOrDefault(inventory =>
                inventory != null &&
                inventory.Name == name &&
                inventory.OwnerEntityId == ownerId);
            if (existing != null)
            {
                extendedBackpackInventoryId = existing.Id;
                return EnsureExtendedBackpackSlotCount(existing);
            }

            Guid stableId = CreateStableGuid(name);
            if (ServerGameState.TryGetInventory(stableId, out SharedInventoryModel? stableExisting) && stableExisting != null)
            {
                extendedBackpackInventoryId = stableId;
                return EnsureExtendedBackpackSlotCount(stableExisting);
            }

            SharedInventoryModel created = SimpleInventorySController.CreateNewInventory(new CreateNewInventoryMessage
            {
                NewId = stableId,
                InventorySize = ExtendedBackpackSlots,
                InventoryType = InventoryType.Default,
                NameId = name,
                FilteredItemFlags = ItemFlag.NoFlags,
                Money = null,
                SlotsFilters = null,
                OwnerEntityId = ownerId,
                Items = null
            }, isTemporary: false);
            extendedBackpackInventoryId = created.Id;
            return created;
        }
        catch
        {
            return null;
        }
    }

    private static SharedInventoryModel EnsureExtendedBackpackSlotCount(SharedInventoryModel inventory)
    {
        if (inventory.InventorySlots.Length != ExtendedBackpackSlots)
        {
            ItemInstanceModel?[] slots = new ItemInstanceModel?[ExtendedBackpackSlots];
            Array.Copy(inventory.InventorySlots, slots, Math.Min(inventory.InventorySlots.Length, slots.Length));
            inventory.InventorySlots = slots;
        }

        inventory.InventoryType = InventoryType.Default;
        inventory.Temporary = false;
        inventory.SlotsFilter = null;
        inventory.FilterItemFlags = ItemFlag.NoFlags;
        return inventory;
    }

    private static Guid CreateStableGuid(string value)
    {
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static void DrawInventorySlotTable(SimpleInventory inventory)
    {
        Vector2 tableSize = new(0f, 330f);
        ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("romstar-inventory-slots", 5, flags, tableSize))
        {
            return;
        }

        ImGui.TableSetupColumn(T("槽位", "Slot"), ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn(T("图标", "Icon"), ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn(T("物品", "Item"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(T("数量", "Qty"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        string term = inventorySearch.Trim();
        for (int i = 0; i < inventory.Model.InventorySlots.Length; i++)
        {
            ItemInstanceModel? item = inventory.Model.InventorySlots[i];
            if (!InventorySlotMatchesFilter(i, item, term))
            {
                continue;
            }

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            bool selected = selectedInventorySlot == i;
            if (ImGui.Selectable($"#{i + 1}##inventory-slot-{i}", selected, ImGuiSelectableFlags.SpanAllColumns))
            {
                selectedInventorySlot = i;
            }

            ImGui.TableSetColumnIndex(1);
            if (item != null)
            {
                DrawIconImage(item.Data.Icon, 28f);
            }
            else
            {
                DrawIconImage(null, 28f);
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(item == null ? T("空", "Empty") : SafeItemName(item));
            ImGui.TableSetColumnIndex(3);
            ImGui.Text(item?.StackCount.ToString() ?? "-");
            ImGui.TableSetColumnIndex(4);
            ImGui.TextWrapped(item?.BaseDataId ?? "-");
        }

        ImGui.EndTable();
    }

    private static bool InventorySlotMatchesFilter(int slotIndex, ItemInstanceModel? item, string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        if (item == null)
        {
            return (slotIndex + 1).ToString().Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        return (slotIndex + 1).ToString().Contains(term, StringComparison.OrdinalIgnoreCase) ||
            item.BaseDataId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            SafeItemName(item).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            CleanInternalId(item.BaseDataId).Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawSelectedInventoryItemDetails(SimpleInventory inventory)
    {
        if (selectedInventorySlot < 0 || selectedInventorySlot >= inventory.Model.InventorySlots.Length)
        {
            ImGui.TextDisabled(T("选择一个背包槽位后，可以复制物品 ID。", "Select a backpack slot to copy its item ID."));
            return;
        }

        ItemInstanceModel? item = inventory.Model.InventorySlots[selectedInventorySlot];
        if (item == null)
        {
            ImGui.TextDisabled(T("当前槽位为空。", "The selected slot is empty."));
            return;
        }

        DrawToolSectionHeader(T("选中物品", "Selected Item"));
        DrawIconImage(item.Data.Icon, 34f);
        ImGui.SameLine();
        ImGui.TextWrapped($"{SafeItemName(item)} x{item.StackCount}");
        ImGui.TextWrapped("ID: " + item.BaseDataId);
        ImGui.TextWrapped(T("实例：", "Instance: ") + item.Id);

        if (DrawGameButton(T("复制物品 ID", "Copy Item ID"), new Vector2(ActionButtonWidth, 30f)))
        {
            ImGui.SetClipboardText(item.BaseDataId);
            status = T("已复制物品 ID：", "Copied item ID: ") + item.BaseDataId;
        }
    }

    private static void DrawSpawnerTab()
    {
        DrawPageTitle(T("生成器", "Spawner"));
        ImGui.TextWrapped(T(
            "提供带图标的物品、建筑和 Boss 生成入口；没有稳定图标的数据保持文字列表。",
            "Provides icon-backed item, construction, and boss spawners; data without stable icons stays text-only."));
        ImGui.Separator();

        DrawToolSectionHeader(T("生成物品到背包", "Spawn Items To Backpack"));
        if (!TryEnsureCatalogLoaded(allowBuild: false))
        {
            DrawAnimatedLoadingText(T("物品目录正在加载，请稍候", "Item catalog is loading. Please wait"));
        }
        else
        {
            if (DrawItemCategorySelector(T("分类", "Category"), "generator-category", ref selectedGeneratorCategory))
            {
                ApplyGeneratorFilter();
                selectedGeneratorItem = 0;
            }

            if (DrawSearchInput(T("搜索生成物", "Search Spawn Item"), "generator-search", ref generatorSearch))
            {
                ApplyGeneratorFilter();
                selectedGeneratorItem = 0;
            }

            DrawNumberInput(T("数量", "Amount"), "generator-amount", ref generatorAmount, 1, MaxCommandAmount);

            if (filteredGeneratorItems.Count > 0)
            {
                DrawItemSelector(T("生成列表", "Spawn List"), "generator-list", filteredGeneratorItems, ref selectedGeneratorItem, GetCatalogDisplayName);

                if (ImGui.Button(T("生成到背包", "Spawn To Backpack"), new Vector2(ActionButtonWidth, 32f)))
                {
                    CatalogItem item = filteredGeneratorItems[selectedGeneratorItem];
                    status = RunCommand($"item {item.Id} {generatorAmount}");
                }
                ImGui.SameLine();
                if (DrawGameButton(T("生成到脚下", "Spawn At Feet"), new Vector2(ActionButtonWidth, 32f)))
                {
                    CatalogItem item = filteredGeneratorItems[selectedGeneratorItem];
                    status = SpawnWorldItem(item.Id, generatorAmount);
                }
            }
            else
            {
                ImGui.TextWrapped(T("没有匹配的生成物。", "No matching spawn items."));
            }
        }

        ImGui.Separator();
        DrawToolSectionHeader(T("建筑 / Boss / 袭击", "Construction / Boss / Raid"));
        ImGui.TextWrapped(T("建筑和 Boss 有游戏图标；袭击数据暂无稳定图标字段。", "Constructions and bosses use game icons; raids have no stable icon field yet."));
        DrawIdDropdownCommand(
            T("搜索建筑", "Search Construction"),
            T("建筑列表", "Construction List"),
            T("生成建筑", "Spawn Construction"),
            "construction-search",
            ref constructionSearch,
            ref selectedConstruction,
            GetConstructionIds,
            ConstructionDisplayName,
            "construct",
            ConstructionIconId);
        DrawIdDropdownCommand(
            T("搜索 Boss", "Search Boss"),
            T("Boss 列表", "Boss List"),
            T("生成 Boss", "Spawn Boss"),
            "boss-search",
            ref bossSearch,
            ref selectedBoss,
            GetBossIds,
            BossDisplayName,
            "boss",
            BossIconId);
        DrawIdDropdownCommand(
            T("搜索袭击", "Search Raid"),
            T("袭击列表", "Raid List"),
            T("生成袭击", "Spawn Raid"),
            "raid-search",
            ref raidSearch,
            ref selectedRaid,
            GetRaidIds,
            RaidDisplayName,
            "raid_new");

        ImGui.Separator();
        DrawToolSectionHeader(T("高级命令入口", "Advanced Command Entry"));
        ImGui.TextWrapped(T("用于直接输入内部 ID。", "For direct internal ID input."));
        DrawIdDropdownCommand(
            T("搜索实体", "Search Entity"),
            T("实体列表", "Entity List"),
            T("生成实体", "Spawn Entity"),
            "entity-search",
            ref entitySearch,
            ref selectedEntity,
            GetSpawnEntityIds,
            EntityDisplayName,
            "spawn",
            EntityIconId,
            drawIcon: DrawEntitySpriteImage,
            enableMousePlacement: true);
        DrawCommandInputRow(T("生成实体 ID（手动）", "Spawn Entity ID (Manual)"), "spawn", ref spawnId);
        DrawCommandInputRow(T("建造/生成建筑", "Construct/Spawn Construction"), "construct", ref constructId);
        DrawCommandInputRow(T("生成 Boss", "Spawn Boss"), "boss", ref bossId);
        DrawCommandInputRow(T("创建袭击", "Create Raid"), "raid_new", ref raidId);

        ImGui.Separator();
        if (ImGui.Button(T("添加普通 NPC", "Add Generic NPC")))
        {
            status = RunCommand("citizen_add_generic");
        }
        ImGui.SameLine();
        if (ImGui.Button(T("刷一波敌人", "Spawn Enemy Wave")))
        {
            status = RunCommand("cheat_spawn_wave");
        }
    }

    private static void DrawPlayerTab()
    {
        DrawPageTitle(T("玩家", "Player"));
        ImGui.TextWrapped(T(
            "玩家属性、移动和常用角色命令。",
            "Player stats, movement, and common character commands."));
        ImGui.Separator();

        DrawPlayerStatsEditor();
        ImGui.Separator();

        DrawToolSectionHeader(T("快捷保持", "Quick Keeps"));
        DrawSustainedPlayerIntRow(
            T("生命值", "Health"),
            "player-health",
            ref playerHealth,
            1,
            999999,
            SetPlayerHealth,
            ref keepPlayerHealth,
            EnableKeepPlayerHealth,
            DisableKeepPlayerHealth);

        DrawSustainedPlayerIntRow(
            T("当前能量", "Current Energy"),
            "player-energy",
            ref playerEnergy,
            0,
            999999,
            SetPlayerEnergy,
            ref keepPlayerEnergy,
            EnableKeepPlayerEnergy,
            DisableKeepPlayerEnergy);

        DrawSustainedPlayerIntRow(
            T("最大能量", "Max Energy"),
            "player-max-energy",
            ref playerMaxEnergy,
            1,
            999999,
            value => SetPlayerBaseStat("Energy", value, T("最大能量", "Max Energy")),
            ref keepPlayerMaxEnergy,
            EnableKeepPlayerMaxEnergy,
            DisableKeepPlayerMaxEnergy);

        DrawSustainedPlayerIntRow(
            T("能量恢复", "Energy Regen"),
            "player-energy-regen",
            ref playerEnergyRegeneration,
            0,
            999999,
            value => SetPlayerBaseStat("EnergyRegeneration", value, T("能量恢复", "Energy Regen")),
            ref keepPlayerEnergyRegeneration,
            EnableKeepPlayerEnergyRegeneration,
            DisableKeepPlayerEnergyRegeneration);

        DrawSustainedPlayerIntRow(
            T("攻击速度", "Attack Speed"),
            "player-attack-speed",
            ref playerAttackSpeed,
            1,
            999999,
            value => SetPlayerBaseStat("AttackSpeed", value, T("攻击速度", "Attack Speed")),
            ref keepPlayerAttackSpeed,
            EnableKeepPlayerAttackSpeed,
            DisableKeepPlayerAttackSpeed);

        DrawSustainedPlayerIntRow(
            T("移动速度倍率", "Move Speed Multiplier"),
            "player-move-speed",
            ref speedMultiplier,
            1,
            20,
            SetPlayerMoveSpeed,
            ref keepPlayerMoveSpeed,
            EnableKeepPlayerMoveSpeed,
            DisableKeepPlayerMoveSpeed);

        if (ImGui.Button(T("浮空/重力开关", "Float/Gravity Toggle")))
        {
            status = RunCommand("float");
        }
        ImGui.SameLine();
        if (ImGui.Button(T("切换自由相机", "Toggle Freecam")))
        {
            status = RunCommand("freecam");
        }

        if (ImGui.Button(T("解锁全部奖励", "Unlock All Rewards")))
        {
            status = RunCommand("rewards_unlock_all");
        }
        ImGui.SameLine();
        if (ImGui.Button(T("遗忘全部恩惠", "Unlearn All Favours")))
        {
            status = RunCommand("unlearn_all_favours");
        }
    }

    private static void DrawPlayerStatsEditor()
    {
        DrawToolSectionHeader(T("玩家属性增强", "Enhanced Player Stats"));
        ImGui.TextWrapped(T(
            "参考游戏原生属性页分组；“应用”只改当前数值，“保持”会持续补回并同步服务端。",
            "Grouped like the native stats page; Apply changes the current value, Keep continuously reapplies it and syncs the server entity."));

        if (DrawGameButton(T("清除全部属性保持", "Clear All Stat Keeps"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = ClearPlayerPersistentStatOverrides();
        }

        if (!ImGui.BeginTabBar("##player-stats-editor-tabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem(T("伤害属性", "Damage")))
        {
            DrawPlayerStatsEditorTab(PlayerStatsTab.Damage, PlayerDamageStats);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("全局属性", "General")))
        {
            DrawPlayerStatsEditorTab(PlayerStatsTab.General, PlayerGeneralStats);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(T("抗性属性", "Resistances")))
        {
            DrawPlayerStatsEditorTab(PlayerStatsTab.Resistances, PlayerResistanceStats);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private static void DrawPlayerStatsEditorTab(PlayerStatsTab tab, IReadOnlyList<PlayerStatEditorDef> stats)
    {
        ImGui.PushID("player-stats-tab-" + tab);
        EntityWrapper? player = Globals.Game?.Player;
        if (player == null)
        {
            ImGui.TextWrapped(T("请先进入存档。", "Enter a save first."));
            ImGui.PopID();
            return;
        }

        for (int i = 0; i < stats.Count; i++)
        {
            DrawPlayerStatEditorRow(player, stats[i]);
        }

        ImGui.PopID();
    }

    private static void DrawPlayerStatEditorRow(EntityWrapper player, PlayerStatEditorDef stat)
    {
        ImGui.PushID("player-stat-" + stat.Id);
        string label = T(stat.ChineseName, stat.EnglishName);
        float current = GetPlayerStatValue(player, stat.Id);
        if (!playerStatDraftValues.TryGetValue(stat.Id, out int value))
        {
            value = Math.Clamp((int)MathF.Round(current), stat.Min, stat.Max);
            playerStatDraftValues[stat.Id] = value;
        }

        DrawFormLabel(label);
        ImGui.TextDisabled(T($"当前 {current:0.##}", $"Current {current:0.##}"));
        ImGui.SameLine(FormLabelWidth + 108f);
        ImGui.SetNextItemWidth(128f);
        if (ImGui.InputInt("##value", ref value))
        {
            value = Math.Clamp(value, stat.Min, stat.Max);
            playerStatDraftValues[stat.Id] = value;
        }

        value = Math.Clamp(value, stat.Min, stat.Max);
        playerStatDraftValues[stat.Id] = value;
        ImGui.SameLine();
        if (ImGui.Button(T("应用", "Apply"), new Vector2(SmallActionButtonWidth, 28f)))
        {
            status = SetPlayerPersistentStat(stat, value, playerPersistentStatOverrides.ContainsKey(stat.Id));
        }

        ImGui.SameLine();
        bool keep = playerPersistentStatOverrides.ContainsKey(stat.Id);
        if (ImGui.Checkbox(T("保持", "Keep") + "##keep", ref keep))
        {
            status = keep
                ? SetPlayerPersistentStat(stat, value, true)
                : DisablePlayerPersistentStat(stat.Id, restoreOriginal: true);
        }

        ImGui.PopID();
    }

    private static void DrawCharacterTab()
    {
        DrawPageTitle(T("角色", "Character"));
        PlayerCharacterModel? character = GetLocalPlayerCharacter();
        if (character == null)
        {
            ImGui.TextWrapped(T("请先进入存档。", "Enter a save first."));
            return;
        }

        if (!favourPointsInitialized)
        {
            favourPoints = Math.Clamp(character.Skills.CurrentFavourPoints, 0, 999999);
            worshipPoints = Math.Clamp(GameState.WorshipPoints, 0, 999999);
            favourPointsInitialized = true;
        }

        DrawToolSectionHeader(T("恩惠 / 崇拜", "Favour / Worship"));
        DrawNumberInput(T("恩惠点", "Favour Points"), "favour-points", ref favourPoints, 0, 999999);
        if (DrawGameButton(T("应用恩惠点", "Apply Favour"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = SetFavourPoints(favourPoints);
        }

        DrawNumberInput(T("崇拜点", "Worship Points"), "worship-points", ref worshipPoints, 0, 999999);
        if (DrawGameButton(T("应用崇拜点", "Apply Worship"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = SetWorshipPoints(worshipPoints);
        }

        ImGui.Separator();
        DrawToolSectionHeader(T("奖励 / 恩惠树", "Rewards / Favour Tree"));
        if (DrawGameButton(T("重置全部恩惠", "Reset Favours"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = RunCommand("unlearn_all_favours");
        }
        ImGui.SameLine();
        if (DrawGameButton(T("解锁全部奖励", "Unlock Rewards"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = RunCommand("rewards_unlock_all");
        }

        ImGui.Separator();
        DrawToolSectionHeader(T("快捷入口", "Shortcuts"));
        ImGui.TextDisabled(T("技能等级编辑已经移到技能页，避免角色页过度拥挤。", "Skill level editing lives on the Skills page to keep this page compact."));
    }

    private static void DrawSkillsTab()
    {
        DrawPageTitle(T("技能", "Skills"));
        ImGui.TextWrapped(T(
            "点击技能行右侧的修改按钮，只展开当前技能的操作；经验增加优先走原版升级逻辑。",
            "Click Edit on a skill row to expand only that skill's actions; added XP uses the game's native leveling path."));
        ImGui.Separator();

        PlayerCharacterModel? character = GetLocalPlayerCharacter();
        if (character == null)
        {
            ImGui.TextWrapped(T("玩家角色尚未就绪。请进入存档后再使用技能功能。", "Player character is not ready. Enter a save before using skill tools."));
            return;
        }

        List<CharacterSkill> skills = GetFilteredPlayerSkills(character).ToList();
        if (DrawSearchInput(T("搜索技能", "Search Skills"), "skill-search", ref skillSearch))
        {
            selectedSkill = 0;
        }

        if (skills.Count == 0)
        {
            ImGui.TextWrapped(T("没有匹配技能。", "No matching skills."));
            return;
        }

        selectedSkill = Math.Clamp(selectedSkill, 0, skills.Count - 1);

        ImGui.Separator();
        DrawToolSectionHeader(T("当前技能", "Current Skills"));
        DrawSkillActionList(character, skills);
        DrawSkillEditorPopup(character, skills);

        ImGui.Separator();
        if (ImGui.Button((showSkillAdvanced ? T("隐藏高级功能", "Hide Advanced") : T("显示高级功能", "Show Advanced")) + "##skill-advanced", new Vector2(ActionButtonWidth, 30f)))
        {
            showSkillAdvanced = !showSkillAdvanced;
        }

        if (showSkillAdvanced)
        {
            DrawSkillAdvancedControls();
        }
    }

    private static void DrawNpcTab()
    {
        DrawPageTitle("NPC");
        if (ServerGameState.Citizens == null || ServerGameState.Citizens.Count == 0)
        {
            ImGui.TextWrapped(T("请先进入存档，或当前没有可修改的 NPC。", "Enter a save first, or there are no editable NPCs."));
            return;
        }

        if (DrawSearchInput(T("搜索 NPC", "Search NPC"), "citizen-search", ref citizenSearch))
        {
            selectedCitizen = 0;
        }

        List<CitizenModel> citizens = ServerGameState.Citizens.Values
            .Where(citizen => citizen != null && MatchesCitizenSearch(citizen))
            .OrderBy(CitizenDisplayName)
            .Take(500)
            .ToList();

        if (citizens.Count == 0)
        {
            ImGui.TextWrapped(T("没有匹配的 NPC。", "No matching NPCs."));
            return;
        }

        selectedCitizen = Math.Clamp(selectedCitizen, 0, citizens.Count - 1);
        CitizenModel selected = citizens[selectedCitizen];
        DrawCitizenSelector(citizens, ref selectedCitizen);
        selected = citizens[selectedCitizen];

        ImGui.Separator();
        DrawCitizenBasicEditor(selected);
        ImGui.Separator();
        DrawCitizenJobEditor(selected);
        ImGui.Separator();
        DrawCitizenStatsEditor(selected);
        ImGui.Separator();
        DrawCitizenAuraEditor(selected);
        ImGui.Separator();
        if (DrawGameButton(T("清除当前 NPC 持续保持", "Clear Current NPC Keeps"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = ClearCitizenPersistentOverrides(selected.Id);
        }
        ImGui.SameLine();
        if (DrawGameButton(T("清除全部 NPC 持续保持", "Clear All NPC Keeps"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = ClearCitizenPersistentOverrides();
        }
    }

    private static void DrawWorldSystemTab()
    {
        DrawPageTitle(T("世界/系统", "World/System"));
        DrawToolSectionHeader(T("时间", "Time"));
        DrawIntApplyRow(T("全局时间倍率", "Global Time Scale"), "game-time-scale", ref gameTimeScale, 0, 100, value => RunCommand($"gametimescale {value}"));
        DrawIntApplyRow(T("昼夜循环倍率", "Day/Night Scale"), "day-night-scale", ref dayNightScale, 0, 100, value => RunCommand($"timescale {value}"));
        DrawIntApplyRow(T("设置小时", "Set Hour"), "hour-to-set", ref hourToSet, 0, 23, value => RunCommand($"hour {value}"));
        if (DrawGameButton(T("跳到次日早晨", "Next Morning"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = RunCommand("nextday");
        }
        ImGui.SameLine();
        if (DrawGameButton(T("重置时间", "Reset Time"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = RunCommand("resettime");
        }

        ImGui.Separator();
        DrawToolSectionHeader(T("系统", "System"));
        if (DrawGameButton(T("保存游戏", "Save Game"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = RunCommand("save_game");
        }
        ImGui.SameLine();
        if (DrawGameButton(T("保存角色", "Save Character"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = RunCommand("save_character");
        }

        ImGui.Separator();
        DrawWeatherControls();

        ImGui.Separator();
        DrawToolSectionHeader(T("地图 / 位置", "Map / Position"));
        if (Globals.Game?.Player != null)
        {
            Microsoft.Xna.Framework.Vector2 current = Globals.Game.Player.Position2;
            ImGui.TextDisabled(T($"当前位置：X {current.X:0.0}, Y {current.Y:0.0}", $"Current Position: X {current.X:0.0}, Y {current.Y:0.0}"));
        }
        DrawNumberInput(T("位置槽位", "Position Slot"), "position-slot", ref positionSlot, 1, 9);
        if (DrawGameButton(T("保存当前位置", "Save Position"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = SavePosition(positionSlot);
        }
        ImGui.SameLine();
        if (DrawGameButton(T("读取位置", "Load Position"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = LoadPosition(positionSlot);
        }

        DrawFormLabel(T("手动 X", "Manual X"));
        ImGui.SetNextItemWidth(SmallNumberWidth);
        ImGui.InputFloat("##manual-position-x", ref manualPositionX, 16f, 128f, "%.1f");
        DrawFormLabel(T("手动 Y", "Manual Y"));
        ImGui.SetNextItemWidth(SmallNumberWidth);
        ImGui.InputFloat("##manual-position-y", ref manualPositionY, 16f, 128f, "%.1f");
        DrawFormLabel("");
        if (DrawGameButton(T("读取当前位置到输入框", "Use Current Position"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = CopyCurrentPositionToManualFields();
        }
        ImGui.SameLine();
        if (DrawGameButton(T("传送到手动坐标", "Teleport Manual"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = TeleportToManualPosition();
        }

        DrawFormLabel("");
        if (DrawGameButton(T("开图/揭开迷雾", "Reveal Map"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = RevealFullMap();
        }
        bool teleportEnabled = mapTeleportEnabled;
        if (DrawCheckboxRow(T("地图右键瞬移", "Map Right-Click Teleport"), ref teleportEnabled))
        {
            mapTeleportEnabled = teleportEnabled;
            status = teleportEnabled
                ? T("已开启地图右键瞬移。", "Map right-click teleport enabled.")
                : T("已关闭地图右键瞬移。", "Map right-click teleport disabled.");
        }
        DrawFormLabel("");
        if (DrawGameButton(T("打开世界地图", "Open World Map"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = OpenWorldMapFromTrainer();
        }
        ImGui.SameLine();
        if (DrawGameButton(T("瞬移到鼠标地图位置", "Teleport To Map Cursor"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = TeleportToRecordedMapPointer();
        }
        ImGui.TextDisabled(T(
            "打开世界地图后，把鼠标放到目标位置并点击右键；若右键未触发，可先移动鼠标再点“瞬移到鼠标地图位置”。",
            "Open the world map, move the cursor to a target, then right-click. If right-click does not trigger, move the cursor and use Teleport To Map Cursor."));
    }

    private static void DrawWeatherControls()
    {
        DrawToolSectionHeader(T("天气", "Weather"));
        if (DrawSearchInput(T("搜索天气", "Search Weather"), "weather-search", ref weatherSearch))
        {
            selectedWeather = 0;
        }

        List<IdDropdownEntry> entries = FilterIdDropdownEntries(
            GetIdDropdownEntries(
                "weather-list",
                static () => ClientWeatherDatabase.Data.Keys,
                WeatherDisplayName,
                null),
            weatherSearch).Take(100).ToList();
        if (entries.Count == 0)
        {
            ImGui.TextWrapped(T("没有可用天气。请先进入存档后再打开此页。", "No weather entries are available. Enter a save before opening this page."));
            return;
        }

        selectedWeather = Math.Clamp(selectedWeather, 0, entries.Count - 1);
        DrawFormLabel(T("天气列表", "Weather List"));
        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##weather-list", entries[selectedWeather].DisplayName))
        {
            for (int i = 0; i < entries.Count; i++)
            {
                bool selected = i == selectedWeather;
                if (ImGui.Selectable(entries[i].DisplayName, selected))
                {
                    selectedWeather = i;
                    ImGui.CloseCurrentPopup();
                }
                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        DrawFormLabel("");
        if (DrawGameButton(T("设置天气", "Set Weather"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = ApplyWeather(entries[selectedWeather].Id, clearOtherWeather: true);
        }
        ImGui.SameLine();
        if (DrawGameButton(T("叠加天气", "Add Weather"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = ApplyWeather(entries[selectedWeather].Id, clearOtherWeather: false);
        }

        bool pauseWeather = GameState.Config.PauseWeatherSystem;
        if (DrawCheckboxRow(T("暂停天气", "Pause Weather"), ref pauseWeather))
        {
            status = SetPauseWeatherSystem(pauseWeather);
        }
    }

    private static string WeatherDisplayName(string weatherId)
    {
        string englishName = CleanInternalId(weatherId);
        string chineseName = weatherId switch
        {
            "normal" => "晴天",
            "rainy" => "雨天",
            "thunder" => "雷暴",
            "fog" => "雾",
            "owl_shadow" => "枭影",
            "sandstorm" => "沙尘暴",
            "ash_rain" => "灰烬雨",
            _ => englishName
        };

        return currentLanguage == UiLanguage.English
            ? $"{englishName} [{weatherId}]"
            : $"{chineseName} / {englishName} [{weatherId}]";
    }

    private static string ApplyWeather(string weatherId, bool clearOtherWeather)
    {
        try
        {
            EntityWrapper? player = Globals.Game?.Player;
            if (player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            if (!ClientWeatherDatabase.Data.ContainsKey(weatherId))
            {
                return T($"找不到天气：{weatherId}。", $"Weather not found: {weatherId}.");
            }

            WeatherService.SendCheatWeather(new CheatWeatherMessage
            {
                Position = player.Position2,
                WeatherId = weatherId,
                Velocity = XnaVector2.Zero,
                ClearOtherWeather = clearOtherWeather
            });

            string name = WeatherDisplayName(weatherId);
            return clearOtherWeather
                ? T($"已请求服务器设置天气：{name}。", $"Requested server to set weather: {name}.")
                : T($"已请求服务器叠加天气：{name}。", $"Requested server to add weather: {name}.");
        }
        catch (Exception ex)
        {
            return T("设置天气失败：", "Failed to set weather: ") + ex.Message;
        }
    }

    private static string SetPauseWeatherSystem(bool enabled)
    {
        try
        {
            GameState.Config.PauseWeatherSystem = enabled;
            ServerGameState.Config.PauseWeatherSystem = enabled;
            GameStateService.Send_UpdateGameConfig(GameState.Config);
            return enabled
                ? T("已暂停天气系统。", "Weather system paused.")
                : T("已恢复天气系统。", "Weather system resumed.");
        }
        catch (Exception ex)
        {
            return T("设置暂停天气失败：", "Failed to set pause weather: ") + ex.Message;
        }
    }

    private static void DrawAffixesTab()
    {
        DrawPageTitle(T("装备词条", "Affixes"));
        ImGui.TextWrapped(T(
            "选择背包或已装备物品，添加/移除装备词条并重新计算属性。",
            "Choose backpack or equipped items, add/remove item affixes, and recalculate stats."));

        string[] sources = currentLanguage == UiLanguage.English
            ? new[] { "Backpack", "Equipped" }
            : new[] { "背包", "已装备" };
        DrawFormLabel(T("来源", "Source"));
        ImGui.SetNextItemWidth(160f);
        ImGui.Combo("##equipment-source", ref equipmentSource, sources, sources.Length);

        if (DrawSearchInput(T("搜索装备", "Search Equipment"), "equipment-search", ref equipmentSearch))
        {
            selectedEquipmentItem = 0;
        }

        List<EquipmentItemRef> equipment = GetEquipmentItems(equipmentSource == 0)
            .Where(item => string.IsNullOrWhiteSpace(equipmentSearch) ||
                EquipmentItemDisplay(item).Contains(equipmentSearch, StringComparison.OrdinalIgnoreCase) ||
                item.Item.BaseDataId.Contains(equipmentSearch, StringComparison.OrdinalIgnoreCase))
            .Take(500)
            .ToList();

        if (equipment.Count == 0)
        {
            ImGui.TextWrapped(T("没有找到可修改词条的武器或装备。", "No editable weapons or equipment were found."));
            return;
        }

        selectedEquipmentItem = Math.Clamp(selectedEquipmentItem, 0, equipment.Count - 1);
        EquipmentItemRef itemRef = equipment[selectedEquipmentItem];
        DrawEquipmentSelector(equipment, ref selectedEquipmentItem);
        itemRef = equipment[selectedEquipmentItem];
        ItemInstanceModel item = itemRef.Item;

        ImGui.Separator();
        DrawIconImage(item.Data?.Icon, 28f);
        ImGui.SameLine();
        ImGui.TextWrapped($"{SafeItemName(item)} [{item.BaseDataId}]");

        DrawCurrentItemAuras(item);
        ImGui.Separator();
        DrawItemAuraStatEditor(item);
        ImGui.Separator();
        DrawAddItemAura(item);
        ImGui.Separator();
        if (DrawGameButton(T("重新计算装备属性", "Recalculate Item Stats"), new Vector2(ActionButtonWidth, 30f)))
        {
            RecalculateItemEverywhere(item);
            status = T("已重新计算装备属性。", "Item stats recalculated.");
        }
    }

    private static void DrawEquipmentSelector(List<EquipmentItemRef> equipment, ref int selectedIndex)
    {
        EquipmentItemRef current = equipment[selectedIndex];
        DrawFormLabel(T("装备列表", "Equipment List"));
        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##equipment-list", EquipmentItemDisplay(current)))
        {
            for (int i = 0; i < equipment.Count; i++)
            {
                bool selected = i == selectedIndex;
                if (ImGui.Selectable(EquipmentItemDisplay(equipment[i]), selected))
                {
                    selectedIndex = i;
                    ImGui.CloseCurrentPopup();
                }
                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }
    }

    private static void DrawCurrentItemAuras(ItemInstanceModel item)
    {
        DrawToolSectionHeader(T("当前词条", "Current Affixes"));
        if (item.Auras.Count == 0)
        {
            ImGui.TextDisabled(T("当前没有词条。", "This item has no affixes."));
            return;
        }

        for (int i = 0; i < item.Auras.Count; i++)
        {
            AbstractAura<ItemData> aura = item.Auras[i];
            ImGui.PushID("item-aura-" + i);
            ImGui.TextWrapped(ItemAuraDisplayName(aura.BaseState.BaseAuraId));
            ImGui.SameLine();
            if (DrawGameButton(T("移除", "Remove"), new Vector2(SmallActionButtonWidth, 28f)))
            {
                status = RemoveItemAuraEverywhere(item, i);
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }
    }

    private static void DrawItemAuraStatEditor(ItemInstanceModel item)
    {
        DrawToolSectionHeader(T("编辑已有加成", "Edit Existing Bonuses"));
        List<ItemAuraStatRef> statRefs = GetItemAuraStatRefs(item).ToList();
        if (statRefs.Count == 0)
        {
            ImGui.TextWrapped(T(
                "当前词条没有可直接编辑的属性加成。",
                "Current affixes have no directly editable stat bonuses."));
            return;
        }

        ImGui.TextWrapped(T(
            "这里修改的是已存在词条带来的加成；百分比请填 15 表示 +15%。",
            "Edits existing affix bonuses; enter 15 for +15% percentage values."));
        foreach (ItemAuraStatRef statRef in statRefs)
        {
            ImGui.PushID($"aura-stat-{statRef.AuraIndex}-{statRef.StatId}-{statRef.ValueKind}");
            string key = BuildItemAuraStatOverrideKey(item.Id, statRef);
            if (!itemAuraStatDraftValues.TryGetValue(key, out float value))
            {
                value = statRef.ToDisplayValue();
                itemAuraStatDraftValues[key] = value;
            }

            ImGui.TextWrapped($"{ItemAuraDisplayName(statRef.AuraId)} / {PlayerStatLabel(statRef.StatId)} / {StatValueKindLabel(statRef.ValueKind)}");
            ImGui.SetNextItemWidth(140f);
            if (ImGui.InputFloat("##value", ref value, 0f, 0f, "%.2f"))
            {
                value = Math.Clamp(value, -999999f, 999999f);
                itemAuraStatDraftValues[key] = value;
            }

            ImGui.SameLine();
            if (DrawGameButton(T("应用", "Apply"), new Vector2(SmallActionButtonWidth, 28f)))
            {
                status = SetItemAuraStatEverywhere(item, statRef, value);
            }
            ImGui.PopID();
        }
    }

    private static void DrawAddItemAura(ItemInstanceModel item)
    {
        DrawToolSectionHeader(T("添加词条", "Add Affix"));
        if (DrawSearchInput(T("搜索词条", "Search Affix"), "item-aura-search", ref itemAuraSearch))
        {
            selectedItemAura = 0;
        }

        List<IdDropdownEntry> auras = FilterIdDropdownEntries(
            GetIdDropdownEntries(
                "item-aura-list",
                static () => ItemAuraDataBase.AuraDataMap.Keys,
                ItemAuraDisplayName,
                null),
            itemAuraSearch).Take(400).ToList();

        if (auras.Count == 0)
        {
            ImGui.TextWrapped(T("没有匹配的词条。", "No matching affixes."));
            return;
        }

        selectedItemAura = Math.Clamp(selectedItemAura, 0, auras.Count - 1);
        IdDropdownEntry selected = auras[selectedItemAura];
        DrawFormLabel(T("可添加词条", "Available Affixes"));
        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##item-aura-list", selected.DisplayName))
        {
            for (int i = 0; i < auras.Count; i++)
            {
                bool isSelected = i == selectedItemAura;
                if (ImGui.Selectable(auras[i].DisplayName, isSelected))
                {
                    selectedItemAura = i;
                    ImGui.CloseCurrentPopup();
                }
                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        DrawFormLabel("");
        if (DrawGameButton(T("添加选中词条", "Add Selected Affix"), new Vector2(ActionButtonWidth, 30f)))
        {
            status = ItemAuraDataBase.AuraDataMap.TryGetValue(auras[selectedItemAura].Id, out ItemAura? aura)
                ? AddItemAuraEverywhere(item, aura)
                : T("词条数据不存在。", "Affix data was not found.");
        }
    }

    private static bool MatchesCitizenSearch(CitizenModel citizen)
    {
        string term = citizenSearch.Trim();
        if (term.Length == 0)
        {
            return true;
        }

        return CitizenDisplayName(citizen).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            CitizenSummary(citizen).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            citizen.Id.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (citizen.CurrentJob?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static void DrawCitizenSelector(List<CitizenModel> citizens, ref int selectedIndex)
    {
        CitizenModel current = citizens[selectedIndex];
        DrawFormLabel(T("NPC 列表", "NPC List"));
        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##citizen-list", CitizenSummary(current)))
        {
            for (int i = 0; i < citizens.Count; i++)
            {
                bool selected = i == selectedIndex;
                if (ImGui.Selectable(CitizenSummary(citizens[i]), selected))
                {
                    selectedIndex = i;
                    ImGui.CloseCurrentPopup();
                }
                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }
    }

    private static void DrawCitizenBasicEditor(CitizenModel citizen)
    {
        DrawToolSectionHeader(T("基础", "Basic"));
        ImGui.TextWrapped(T("当前 NPC：", "Current NPC: ") + CitizenSummary(citizen));

        float loyalty = citizen.Loyalty;
        DrawFormLabel(T("忠诚", "Loyalty"));
        ImGui.SetNextItemWidth(SmallNumberWidth);
        if (ImGui.InputFloat("##citizen-loyalty", ref loyalty, 1f, 10f, "%.0f"))
        {
            citizen.Loyalty = Math.Clamp(loyalty, 0f, 999999f);
            SetCitizenPersistentBasicOverride(citizen, "Loyalty", citizen.Loyalty);
            status = T("已修改并持续保持 NPC 忠诚。", "NPC loyalty updated and kept.");
        }

        int loyaltyLevel = citizen.LoyaltyLevel;
        DrawFormLabel(T("忠诚等级", "Loyalty Level"));
        ImGui.SetNextItemWidth(SmallNumberWidth);
        if (ImGui.InputInt("##citizen-loyalty-level", ref loyaltyLevel))
        {
            citizen.LoyaltyLevel = Math.Clamp(loyaltyLevel, 0, 4);
            if (citizen.LoyaltyLevel >= 4)
            {
                citizen.Loyalty = 0f;
                SetCitizenPersistentBasicOverride(citizen, "Loyalty", citizen.Loyalty);
            }
            SetCitizenPersistentBasicOverride(citizen, "LoyaltyLevel", citizen.LoyaltyLevel);
            status = T("已修改并持续保持 NPC 忠诚等级。", "NPC loyalty level updated and kept.");
        }

        float hunger = citizen.CurrentHunger;
        DrawFormLabel(T("饥饿值", "Hunger"));
        ImGui.SetNextItemWidth(SmallNumberWidth);
        if (ImGui.InputFloat("##citizen-hunger", ref hunger, 0.05f, 0.2f, "%.2f"))
        {
            citizen.CurrentHunger = Math.Clamp(hunger, 0f, 1f);
            SetCitizenPersistentBasicOverride(citizen, "CurrentHunger", citizen.CurrentHunger);
            status = T("已修改并持续保持 NPC 饥饿值。", "NPC hunger updated and kept.");
        }
    }

    private static void DrawCitizenJobEditor(CitizenModel citizen)
    {
        DrawToolSectionHeader(T("职业等级", "Job Levels"));
        if (citizen.JobExperience == null || citizen.JobExperience.Count == 0)
        {
            ImGui.TextDisabled(T("这个 NPC 没有职业经验数据。", "This NPC has no job experience data."));
            return;
        }

        foreach (KeyValuePair<string, JobProgress> entry in citizen.JobExperience.OrderBy(entry => JobDisplayName(entry.Key, citizen.IsMale)))
        {
            ImGui.PushID("job-" + entry.Key);
            JobProgress progress = entry.Value;
            string label = JobDisplayName(entry.Key, citizen.IsMale);
            int level = progress.Level;
            DrawFormLabel(label + T(" 等级", " Level"));
            ImGui.SetNextItemWidth(SmallNumberWidth);
            if (ImGui.InputInt("##job-level", ref level))
            {
                progress.Level = Math.Clamp(level, 0, 999);
                SetCitizenPersistentJobOverride(citizen, entry.Key, "Level", progress.Level);
                status = T($"已修改并持续保持 NPC 职业等级：{label} = {progress.Level}", $"NPC job level updated and kept: {label} = {progress.Level}");
            }

            float experience = progress.Experience;
            DrawFormLabel(label + T(" 经验", " XP"));
            ImGui.SetNextItemWidth(SmallNumberWidth);
            if (ImGui.InputFloat("##job-xp", ref experience, 10f, 100f, "%.0f"))
            {
                progress.Experience = Math.Clamp(experience, 0f, 9999999f);
                SetCitizenPersistentJobOverride(citizen, entry.Key, "Experience", progress.Experience);
                status = T("已修改并持续保持 NPC 职业经验：", "NPC job XP updated and kept: ") + label;
            }
            ImGui.PopID();
        }
    }

    private static void DrawCitizenStatsEditor(CitizenModel citizen)
    {
        DrawToolSectionHeader(T("属性", "Stats"));
        if (citizen.CitizenStats.Stats == null || citizen.CitizenStats.Stats.Count == 0)
        {
            ImGui.TextDisabled(T("这个 NPC 没有属性数据。", "This NPC has no stat data."));
            return;
        }

        foreach (KeyValuePair<string, Stat> entry in citizen.CitizenStats.Stats.OrderBy(entry => CitizenStatDisplayName(entry.Key)))
        {
            ImGui.PushID("citizen-stat-" + entry.Key);
            float value = entry.Value.BaseValue;
            string label = CitizenStatDisplayName(entry.Key);
            DrawFormLabel(label);
            ImGui.SetNextItemWidth(SmallNumberWidth);
            if (ImGui.InputFloat("##stat", ref value, 1f, 10f, "%.2f"))
            {
                entry.Value.SetBaseValue(value);
                SetCitizenPersistentStatOverride(citizen, entry.Key, value);
                status = T($"已修改并持续保持 NPC 属性：{label} = {value:0.##}", $"NPC stat updated and kept: {label} = {value:0.##}");
            }
            ImGui.SameLine();
            ImGui.TextDisabled(T($"当前 {entry.Value.CalculatedValue:0.##}", $"Current {entry.Value.CalculatedValue:0.##}"));
            ImGui.PopID();
        }
    }

    private static void DrawCitizenAuraEditor(CitizenModel citizen)
    {
        DrawToolSectionHeader(T("特质 / Aura", "Traits / Auras"));
        citizen.Auras ??= new List<CitizenAuraModel>();

        if (citizen.Auras.Count == 0)
        {
            ImGui.TextDisabled(T("当前没有特质。", "No traits currently assigned."));
        }
        else
        {
            for (int i = 0; i < citizen.Auras.Count; i++)
            {
                CitizenAuraModel aura = citizen.Auras[i];
                ImGui.PushID("citizen-aura-" + i);
                ImGui.TextWrapped(AuraDisplayName(aura.DataId));
                ImGui.SameLine();
                if (DrawGameButton(T("移除", "Remove"), new Vector2(SmallActionButtonWidth, 28f)))
                {
                    citizen.Auras.RemoveAt(i);
                    RebuildCitizenAuraStats(citizen);
                    status = T("已移除 NPC 特质。", "NPC trait removed.");
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }
        }

        if (DrawSearchInput(T("搜索特质", "Search Trait"), "citizen-aura-search", ref citizenAuraSearch))
        {
            selectedCitizenAura = 0;
        }

        List<IdDropdownEntry> traits = FilterIdDropdownEntries(
            GetIdDropdownEntries(
                "citizen-aura-list",
                static () => CitizenAuraDatabase.DataMap.Values
                    .Where(aura => aura.Type == CitizenAuraType.Trait)
                    .Select(aura => aura.Id),
                AuraDisplayName,
                null),
            citizenAuraSearch).Take(300).ToList();

        if (traits.Count == 0)
        {
            ImGui.TextDisabled(T("没有匹配的特质。", "No matching traits."));
            return;
        }

        selectedCitizenAura = Math.Clamp(selectedCitizenAura, 0, traits.Count - 1);
        IdDropdownEntry selected = traits[selectedCitizenAura];
        DrawFormLabel(T("可添加特质", "Available Trait"));
        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##citizen-aura-list", selected.DisplayName))
        {
            for (int i = 0; i < traits.Count; i++)
            {
                bool isSelected = i == selectedCitizenAura;
                if (ImGui.Selectable(traits[i].DisplayName, isSelected))
                {
                    selectedCitizenAura = i;
                    ImGui.CloseCurrentPopup();
                }
                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        DrawFormLabel("");
        if (DrawGameButton(T("添加选中特质", "Add Selected Trait"), new Vector2(ActionButtonWidth, 30f)))
        {
            if (CitizenAuraDatabase.DataMap.TryGetValue(traits[selectedCitizenAura].Id, out CitizenAuraInfo? trait))
            {
                AddCitizenAura(citizen, trait);
                status = T("已添加 NPC 特质：", "NPC trait added: ") + traits[selectedCitizenAura].DisplayName;
            }
            else
            {
                status = T("特质数据不存在。", "Trait data was not found.");
            }
        }
    }

    private static string AuraDisplayName(string auraId)
    {
        return CleanInternalId(auraId) + " [" + auraId + "]";
    }

    private static void AddCitizenAura(CitizenModel citizen, CitizenAuraInfo aura)
    {
        citizen.Auras ??= new List<CitizenAuraModel>();
        CitizenAuraModel model = new(
            Guid.NewGuid(),
            aura.Id,
            aura.StatsToAdd,
            aura.IsBuff,
            aura.Type,
            aura.Duration,
            aura.InstanceTypeId,
            0f,
            fromTown: false);
        citizen.Auras.Add(model);
        citizen.CitizenStats.AddAuraStats(model.Id, model);
    }

    private static void RebuildCitizenAuraStats(CitizenModel citizen)
    {
        // The current game build does not expose a public full reset method for
        // CitizenStats. Added traits are applied immediately; removed traits may
        // require a stat refresh from the game to fully recalculate derived values.
    }

    private static string CitizenSummary(CitizenModel citizen)
    {
        string job = JobDisplayName(citizen.CurrentJob, citizen.IsMale);
        string level = "";
        if (!string.IsNullOrWhiteSpace(citizen.CurrentJob) && citizen.JobExperience != null && citizen.JobExperience.TryGetValue(citizen.CurrentJob, out JobProgress? progress))
        {
            level = $" Lv.{progress.Level}";
        }
        return $"{CitizenDisplayName(citizen)} - {job}{level}";
    }

    private static string CitizenDisplayName(CitizenModel citizen)
    {
        return citizen.Id.ToString();
    }

    private static string JobDisplayName(string? jobId, bool isMale)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return T("无职业", "No Job");
        }

        return CleanInternalId(jobId);
    }

    private static string CitizenStatDisplayName(string statId)
    {
        return statId switch
        {
            "Citizen_Efficiency" => T("效率", "Efficiency"),
            "Citizen_ExperienceGain" => T("经验获取", "Experience Gain"),
            "Citizen_Expertise" => T("专业度", "Expertise"),
            "Citizen_FoodCost" => T("食物消耗", "Food Cost"),
            "Citizen_Happiness" => T("幸福度", "Happiness"),
            "Citizen_LoyaltyGain" => T("忠诚获取", "Loyalty Gain"),
            "Health" => T("生命值", "Health"),
            _ => CleanInternalId(statId)
        };
    }

    private static IEnumerable<EquipmentItemRef> GetEquipmentItems(bool backpack)
    {
        if (backpack)
        {
            if (!GameState.TryGetLocalPlayerInventory(out SimpleInventory? inventory) || inventory == null)
            {
                yield break;
            }

            for (int i = 0; i < inventory.Model.InventorySlots.Length; i++)
            {
                ItemInstanceModel? item = inventory.Model.InventorySlots[i];
                if (IsEditableEquipment(item))
                {
                    yield return new EquipmentItemRef(item!, T("背包", "Backpack"), i);
                }
            }
            yield break;
        }

        if (!GameState.TryGetLocalPlayerEquipmentInventory(out SimpleInventory? equipment) || equipment == null)
        {
            yield break;
        }

        for (int i = 0; i < equipment.Model.InventorySlots.Length; i++)
        {
            ItemInstanceModel? item = equipment.Model.InventorySlots[i];
            if (IsEditableEquipment(item))
            {
                yield return new EquipmentItemRef(item!, T("已装备", "Equipped"), i);
            }
        }
    }

    private static bool IsEditableEquipment(ItemInstanceModel? item)
    {
        return item != null && GetItemDataForInstance(item)?.Equippable != null;
    }

    private static string EquipmentItemDisplay(EquipmentItemRef itemRef)
    {
        return $"{itemRef.Source} #{itemRef.Slot + 1} - {SafeItemName(itemRef.Item)} [{itemRef.Item.BaseDataId}]";
    }

    private static ItemData? GetItemDataForInstance(ItemInstanceModel item)
    {
        try
        {
            return item.Data ?? ItemDataBase.GetItemDataOrNull(item.BaseDataId);
        }
        catch
        {
            return null;
        }
    }

    private static string SafeItemName(ItemInstanceModel item)
    {
        return CleanInternalId(item.BaseDataId);
    }

    private static string ItemAuraDisplayName(string auraId)
    {
        string[] parts = auraId.Split(':');
        if (parts.Length >= 3)
        {
            string group = currentLanguage == UiLanguage.English
                ? CleanInternalId(parts[1])
                : ItemAuraGroupName(parts[1]);
            string name = currentLanguage == UiLanguage.English
                ? CleanInternalId(parts[^1])
                : ItemAuraName(parts[^1]);
            return $"{group} - {name} [{auraId}]";
        }
        return CleanInternalId(auraId) + " [" + auraId + "]";
    }

    private static string ItemAuraGroupName(string id)
    {
        return id switch
        {
            "armor" => "护甲",
            "melee" => "近战",
            "ranged" => "远程",
            "tool" => "工具",
            "trinket" => "饰品",
            _ => CleanInternalId(id)
        };
    }

    private static string ItemAuraName(string id)
    {
        return id switch
        {
            "anti-hex" => "抗诅咒",
            "blunt" => "钝化",
            "bulwark" => "壁垒",
            "damaged" => "破损",
            "dried" => "干燥",
            "forceful" => "强力",
            "hardened" => "硬化",
            "legendary" => "传奇",
            "masterwork" => "大师",
            "precise" => "精准",
            "pristine" => "完美",
            "reaching" => "延伸",
            "rusted" => "生锈",
            "sharpened" => "锋利",
            "swift" => "迅捷",
            "weak" => "脆弱",
            _ => CleanInternalId(id)
        };
    }

    private static string PlayerStatLabel(string statId)
    {
        return statId switch
        {
            "Health" => T("最大生命", "Max Health"),
            "Energy" => T("最大能量", "Max Energy"),
            "EnergyRegeneration" => T("能量恢复", "Energy Regen"),
            "Armor" => T("护甲", "Armor"),
            "MagicResistance" => T("魔法抗性", "Magic Resistance"),
            "MeleeDamage" => T("近战伤害", "Melee Damage"),
            "RangedDamage" => T("远程伤害", "Ranged Damage"),
            "MagicDamage" => T("魔法伤害", "Magic Damage"),
            "MeleeDamageModifier" => T("近战伤害倍率", "Melee Damage Modifier"),
            "RangedDamageModifier" => T("远程伤害倍率", "Ranged Damage Modifier"),
            "MagicDamageModifier" => T("魔法伤害倍率", "Magic Damage Modifier"),
            "ThrowingDamageModifier" => T("投掷伤害倍率", "Throwing Damage Modifier"),
            "SlashingDamageModifier" => T("挥砍伤害倍率", "Slashing Damage Modifier"),
            "BludgeoningDamageModifier" => T("钝击伤害倍率", "Bludgeoning Damage Modifier"),
            "PiercingDamageModifier" => T("穿刺伤害倍率", "Piercing Damage Modifier"),
            "PyroDamageModifier" => T("火焰伤害倍率", "Pyro Damage Modifier"),
            "ChloroDamageModifier" => T("自然伤害倍率", "Chloro Damage Modifier"),
            "AquaDamageModifier" => T("水系伤害倍率", "Aqua Damage Modifier"),
            "CosmoDamageModifier" => T("星界伤害倍率", "Cosmo Damage Modifier"),
            "NecroDamageModifier" => T("死灵伤害倍率", "Necro Damage Modifier"),
            "AttackSpeed" => T("攻击速度", "Attack Speed"),
            "CritChance" => T("暴击率", "Crit Chance"),
            "CritDamage" => T("暴击伤害", "Crit Damage"),
            "PickaxePower" => T("镐力", "Pickaxe Power"),
            "AxePower" => T("斧力", "Axe Power"),
            "KnockbackResistance" => T("击退抗性", "Knockback Resistance"),
            "MovementSpeed" => T("移动速度", "Movement Speed"),
            "AttackRangeModifier" => T("攻击范围倍率", "Attack Range Modifier"),
            "Knockback" => T("击退", "Knockback"),
            "LightSource" => T("光源", "Light Source"),
            "status:bleed" => T("流血抗性", "Bleed Resistance"),
            "status:burning" => T("燃烧抗性", "Burning Resistance"),
            "status:charm" => T("魅惑抗性", "Charm Resistance"),
            "status:confusion" => T("混乱抗性", "Confusion Resistance"),
            "status:disease" => T("疾病抗性", "Disease Resistance"),
            "status:curse" => T("诅咒抗性", "Curse Resistance"),
            "status:petrified" => T("石化抗性", "Petrified Resistance"),
            "status:poisoned" => T("中毒抗性", "Poison Resistance"),
            "status:root" => T("缠绕抗性", "Root Resistance"),
            "status:slow" => T("减速抗性", "Slow Resistance"),
            "status:stun" => T("眩晕抗性", "Stun Resistance"),
            "SlashingResistance" => T("挥砍抗性", "Slashing Resistance"),
            "BludgeoningResistance" => T("钝击抗性", "Bludgeoning Resistance"),
            "PiercingResistance" => T("穿刺抗性", "Piercing Resistance"),
            "PyroResistance" => T("火焰抗性", "Pyro Resistance"),
            "ChloroResistance" => T("自然抗性", "Chloro Resistance"),
            "AquaResistance" => T("水系抗性", "Aqua Resistance"),
            "CosmoResistance" => T("星界抗性", "Cosmo Resistance"),
            "NecroResistance" => T("死灵抗性", "Necro Resistance"),
            _ => CleanInternalId(statId)
        };
    }

    private static string AddItemAuraEverywhere(ItemInstanceModel clientItem, ItemAura aura)
    {
        try
        {
            AddAuraToItemInstance(clientItem, aura.Id);
            int synced = SyncMatchingServerItem(clientItem, serverItem => AddAuraToItemInstance(serverItem, aura.Id));
            SyncMatchingActiveInventoryItem(clientItem);
            return synced > 0
                ? T("已添加并同步装备词条：", "Affix added and synced: ") + ItemAuraDisplayName(aura.Id)
                : T("已添加装备词条，但未找到服务端物品实例；重新装备后可能丢失。", "Affix added, but no server item instance was found; it may disappear after re-equipping.");
        }
        catch (Exception ex)
        {
            return T("添加装备词条失败：", "Failed to add affix: ") + ex.Message;
        }
    }

    private static string RemoveItemAuraEverywhere(ItemInstanceModel clientItem, int auraIndex)
    {
        try
        {
            if (auraIndex < 0 || auraIndex >= clientItem.Auras.Count)
            {
                return T("移除失败：词条索引无效。", "Remove failed: invalid affix index.");
            }

            string auraId = clientItem.Auras[auraIndex].BaseState.BaseAuraId;
            RemoveAuraAt(clientItem, auraIndex);
            RemoveItemAuraStatOverrides(clientItem.Id, auraId);
            int synced = SyncMatchingServerItem(clientItem, serverItem => RemoveFirstAuraById(serverItem, auraId));
            SyncMatchingActiveInventoryItem(clientItem);
            return synced > 0
                ? T("已移除并同步装备词条：", "Affix removed and synced: ") + ItemAuraDisplayName(auraId)
                : T("已移除装备词条，但未找到服务端物品实例；重新装备后可能恢复。", "Affix removed, but no server item instance was found; it may return after re-equipping.");
        }
        catch (Exception ex)
        {
            return T("移除装备词条失败：", "Failed to remove affix: ") + ex.Message;
        }
    }

    private static void RecalculateItemEverywhere(ItemInstanceModel clientItem)
    {
        clientItem.CalculateStats();
        SyncMatchingServerItem(clientItem, serverItem => serverItem.CalculateStats());
        SyncMatchingActiveInventoryItem(clientItem);
    }

    private static void RememberItemAuraStatOverride(Guid itemId, ItemAuraStatRef statRef, float displayValue)
    {
        itemAuraStatOverrides[BuildItemAuraStatOverrideKey(itemId, statRef)] = displayValue;
        RememberItemAuraTemplateStatOverride(statRef, displayValue);
    }

    private static void RemoveItemAuraStatOverrides(Guid itemId, string auraId)
    {
        string prefix = $"{itemId:N}|{auraId}|";
        foreach (string key in itemAuraStatOverrides.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            itemAuraStatOverrides.Remove(key);
            itemAuraStatDraftValues.Remove(key);
        }
    }

    private static void ApplyItemAuraStatOverrides()
    {
        if (itemAuraStatOverrides.Count == 0)
        {
            return;
        }

        try
        {
            foreach (ItemInstanceModel item in GetEquipmentItems(backpack: true)
                .Concat(GetEquipmentItems(backpack: false))
                .Select(itemRef => itemRef.Item)
                .DistinctBy(item => item.Id)
                .ToList())
            {
                ApplyItemAuraStatOverrides(item);
            }
        }
        catch
        {
        }
    }

    private static void ApplyItemAuraStatOverrides(ItemInstanceModel item)
    {
        List<ItemAuraStatRef> statRefs = GetItemAuraStatRefs(item).ToList();
        if (statRefs.Count == 0)
        {
            return;
        }

        bool changed = false;
        foreach (ItemAuraStatRef statRef in statRefs)
        {
            string key = BuildItemAuraStatOverrideKey(item.Id, statRef);
            if (itemAuraStatOverrides.TryGetValue(key, out float displayValue) &&
                Math.Abs(statRef.ToDisplayValue() - displayValue) >= 0.01f &&
                SetItemAuraStat(item, statRef, displayValue))
            {
                SyncMatchingServerItem(item, serverItem => SetItemAuraStat(serverItem, statRef, displayValue));
                changed = true;
            }
        }

        if (changed)
        {
            SyncMatchingActiveInventoryItem(item);
        }
    }

    private static string BuildItemAuraStatOverrideKey(Guid itemId, ItemAuraStatRef statRef)
    {
        return $"{itemId:N}|{statRef.AuraId}|{statRef.StatId}|{statRef.ValueKind}";
    }

    private static void RememberItemAuraTemplateStatOverride(ItemAuraStatRef statRef, float displayValue)
    {
        itemAuraTemplateStatOverrides[BuildItemAuraTemplateStatOverrideKey(statRef)] = displayValue;
        PatchItemAuraTemplate(statRef.AuraId);
    }

    private static string BuildItemAuraTemplateStatOverrideKey(ItemAuraStatRef statRef)
    {
        return BuildItemAuraTemplateStatOverrideKey(statRef.AuraId, statRef.StatId, statRef.ValueKind);
    }

    private static string BuildItemAuraTemplateStatOverrideKey(string auraId, string statId, ItemAuraStatValueKind valueKind)
    {
        return $"{auraId}|{statId}|{valueKind}";
    }

    private static void PatchItemAuraTemplate(string auraId)
    {
        if (!patchedItemAuraTemplates.Add(auraId) || !ItemAuraDataBase.AuraDataMap.TryGetValue(auraId, out ItemAura? aura))
        {
            return;
        }

        Func<AbstractAuraArgs> originalFactory = aura.GetStateArgs;
        aura.GetStateArgs = () =>
        {
            AbstractAuraArgs args = originalFactory();
            ApplyItemAuraTemplateStatOverrides(auraId, args);
            return args;
        };
    }

    private static void ApplyItemAuraTemplateStatOverrides(string auraId, AbstractAuraArgs args)
    {
        if (args is not ItemStatsChangeAuraArgs statArgs || statArgs.StatsToAdd.Count == 0)
        {
            return;
        }

        foreach (string statId in statArgs.StatsToAdd.Keys.ToList())
        {
            StatModificationData data = statArgs.StatsToAdd[statId];
            ApplyItemAuraTemplateStatOverride(auraId, statId, ItemAuraStatValueKind.Additive, ref data.Additive);
            ApplyItemAuraTemplateStatOverride(auraId, statId, ItemAuraStatValueKind.AdditiveMultiplier, ref data.AdditiveMultiplier);
            ApplyItemAuraTemplateStatOverride(auraId, statId, ItemAuraStatValueKind.BaseMultiplier, ref data.BaseMultiplier);
            ApplyItemAuraTemplateStatOverride(auraId, statId, ItemAuraStatValueKind.BonusMultiplier, ref data.BonusMultiplier);
            ApplyItemAuraTemplateStatOverride(auraId, statId, ItemAuraStatValueKind.Multiplier, ref data.Multiplier);
            statArgs.StatsToAdd[statId] = data;
        }
    }

    private static void ApplyItemAuraTemplateStatOverride(string auraId, string statId, ItemAuraStatValueKind valueKind, ref float internalValue)
    {
        string key = BuildItemAuraTemplateStatOverrideKey(auraId, statId, valueKind);
        if (itemAuraTemplateStatOverrides.TryGetValue(key, out float displayValue))
        {
            internalValue = ItemAuraDisplayToInternalValue(valueKind, displayValue);
        }
    }

    private static float ItemAuraDisplayToInternalValue(ItemAuraStatValueKind valueKind, float displayValue)
    {
        return valueKind == ItemAuraStatValueKind.Additive ? displayValue : displayValue / 100f;
    }

    private static IEnumerable<ItemAuraStatRef> GetItemAuraStatRefs(ItemInstanceModel item)
    {
        for (int auraIndex = 0; auraIndex < item.Auras.Count; auraIndex++)
        {
            AbstractAura<ItemData> aura = item.Auras[auraIndex];
            if (aura.BaseState.CurrentArgs is not ItemStatsChangeAuraArgs statArgs || statArgs.StatsToAdd.Count == 0)
            {
                continue;
            }

            foreach (KeyValuePair<string, StatModificationData> entry in statArgs.StatsToAdd)
            {
                StatModificationData data = entry.Value;
                if (data.Additive != 0f)
                {
                    yield return new ItemAuraStatRef(auraIndex, aura.BaseState.BaseAuraId, entry.Key, ItemAuraStatValueKind.Additive, data.Additive);
                }
                if (data.AdditiveMultiplier != 0f)
                {
                    yield return new ItemAuraStatRef(auraIndex, aura.BaseState.BaseAuraId, entry.Key, ItemAuraStatValueKind.AdditiveMultiplier, data.AdditiveMultiplier);
                }
                if (data.BaseMultiplier != 0f)
                {
                    yield return new ItemAuraStatRef(auraIndex, aura.BaseState.BaseAuraId, entry.Key, ItemAuraStatValueKind.BaseMultiplier, data.BaseMultiplier);
                }
                if (data.BonusMultiplier != 0f)
                {
                    yield return new ItemAuraStatRef(auraIndex, aura.BaseState.BaseAuraId, entry.Key, ItemAuraStatValueKind.BonusMultiplier, data.BonusMultiplier);
                }
                if (data.Multiplier != 0f)
                {
                    yield return new ItemAuraStatRef(auraIndex, aura.BaseState.BaseAuraId, entry.Key, ItemAuraStatValueKind.Multiplier, data.Multiplier);
                }
            }
        }
    }

    private static string SetItemAuraStatEverywhere(ItemInstanceModel clientItem, ItemAuraStatRef statRef, float displayValue)
    {
        try
        {
            RememberItemAuraStatOverride(clientItem.Id, statRef, displayValue);
            bool changed = SetItemAuraStat(clientItem, statRef, displayValue);
            int synced = SyncMatchingServerItem(clientItem, serverItem => SetItemAuraStat(serverItem, statRef, displayValue));
            SyncMatchingActiveInventoryItem(clientItem);
            return changed
                ? T($"已修改装备词条加成：{PlayerStatLabel(statRef.StatId)} = {displayValue:0.##}", $"Affix bonus updated: {PlayerStatLabel(statRef.StatId)} = {displayValue:0.##}") +
                    (synced > 0 ? T("，并已同步服务端。", ", and synced to server.") : T("，但未找到服务端物品实例。", ", but no server item instance was found."))
                : T("修改失败：没有找到对应的词条加成。", "Update failed: matching affix bonus was not found.");
        }
        catch (Exception ex)
        {
            return T("修改装备词条加成失败：", "Failed to update affix bonus: ") + ex.Message;
        }
    }

    private static bool SetItemAuraStat(ItemInstanceModel item, ItemAuraStatRef statRef, float displayValue)
    {
        if (statRef.AuraIndex < 0 || statRef.AuraIndex >= item.Auras.Count)
        {
            return false;
        }

        AbstractAura<ItemData> aura = item.Auras[statRef.AuraIndex];
        if (aura.BaseState.BaseAuraId != statRef.AuraId ||
            aura.BaseState.CurrentArgs is not ItemStatsChangeAuraArgs statArgs ||
            !statArgs.StatsToAdd.TryGetValue(statRef.StatId, out StatModificationData data))
        {
            return false;
        }

        float internalValue = statRef.FromDisplayValue(displayValue);
        switch (statRef.ValueKind)
        {
            case ItemAuraStatValueKind.Additive:
                data.Additive = internalValue;
                break;
            case ItemAuraStatValueKind.AdditiveMultiplier:
                data.AdditiveMultiplier = internalValue;
                break;
            case ItemAuraStatValueKind.BaseMultiplier:
                data.BaseMultiplier = internalValue;
                break;
            case ItemAuraStatValueKind.BonusMultiplier:
                data.BonusMultiplier = internalValue;
                break;
            case ItemAuraStatValueKind.Multiplier:
                data.Multiplier = internalValue;
                break;
        }

        statArgs.StatsToAdd[statRef.StatId] = data;
        item.CalculateStats();
        return true;
    }

    private static string StatValueKindLabel(ItemAuraStatValueKind kind)
    {
        return kind switch
        {
            ItemAuraStatValueKind.Additive => T("固定值", "Flat"),
            ItemAuraStatValueKind.AdditiveMultiplier => T("额外百分比", "Additive %"),
            ItemAuraStatValueKind.BaseMultiplier => T("基础百分比", "Base %"),
            ItemAuraStatValueKind.BonusMultiplier => T("奖励百分比", "Bonus %"),
            ItemAuraStatValueKind.Multiplier => T("总倍率百分比", "Multiplier %"),
            _ => kind.ToString()
        };
    }

    private static void AddAuraToItemInstance(ItemInstanceModel item, string auraId)
    {
        ItemAura? aura = ItemAuraDataBase.GetAuraOrNull(auraId);
        if (aura != null)
        {
            item.Auras.Add(new ServerItemStatsChangeAura(aura));
            item.CalculateStats();
        }
    }

    private static void RemoveAuraAt(ItemInstanceModel item, int auraIndex)
    {
        item.Auras.RemoveAt(auraIndex);
        item.CalculateStats();
    }

    private static void RemoveFirstAuraById(ItemInstanceModel item, string auraId)
    {
        int index = item.Auras.FindIndex(aura => aura?.BaseState.BaseAuraId == auraId);
        if (index >= 0)
        {
            RemoveAuraAt(item, index);
            return;
        }
        item.CalculateStats();
    }

    private static int SyncMatchingServerItem(ItemInstanceModel clientItem, Action<ItemInstanceModel> update)
    {
        int count = 0;
        if (ServerGameState.TryGetItemInstance(clientItem.Id, out ItemInstanceModel? serverItem) && serverItem != null)
        {
            update(serverItem);
            count++;
        }

        if (clientItem.InventoryId.HasValue && ServerGameState.TryGetInventory(clientItem.InventoryId.Value, out SharedInventoryModel? inventory) && inventory != null)
        {
            for (int i = 0; i < inventory.InventorySlots.Length; i++)
            {
                ItemInstanceModel? item = inventory.InventorySlots[i];
                if (item?.Id == clientItem.Id && item != serverItem)
                {
                    update(item);
                    count++;
                }
            }
        }
        return count;
    }

    private static void SyncMatchingActiveInventoryItem(ItemInstanceModel source)
    {
        if (!source.InventoryId.HasValue || !GameState.ActiveInventories.TryGetValue(source.InventoryId.Value, out SimpleInventory? inventory) || inventory == null)
        {
            return;
        }

        for (int i = 0; i < inventory.Model.InventorySlots.Length; i++)
        {
            ItemInstanceModel? item = inventory.Model.InventorySlots[i];
            if (item?.Id != source.Id || item == source)
            {
                continue;
            }

            item.Auras.Clear();
            foreach (AbstractAura<ItemData> aura in source.Auras)
            {
                item.Auras.Add(aura);
            }
            item.CalculateStats();
        }
    }

    private static PlayerCharacterModel? GetLocalPlayerCharacter()
    {
        try
        {
            return GameState.LocalPlayer?.Character?.Character;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<CharacterSkill> GetFilteredPlayerSkills(PlayerCharacterModel character)
    {
        IEnumerable<CharacterSkill> query = character.Skills.CharacterSkills.Values;
        string term = skillSearch.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(skill =>
                skill.SkillId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                SkillDisplayName(skill).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                CleanInternalId(skill.SkillId).Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return query.OrderBy(SkillDisplayName);
    }

    private static void DrawSkillActionList(PlayerCharacterModel character, List<CharacterSkill> skills)
    {
        if (ImGui.BeginTable("skill-action-list", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg, new Vector2(0f, 0f)))
        {
            for (int i = 0; i < skills.Count; i += 2)
            {
                ImGui.TableNextRow();
                for (int column = 0; column < 2; column++)
                {
                    int index = i + column;
                    ImGui.TableSetColumnIndex(column);
                    if (index < skills.Count)
                    {
                        DrawSkillActionCell(character, skills[index], index);
                    }
                }
            }
            ImGui.EndTable();
        }
    }

    private static void DrawSkillActionCell(PlayerCharacterModel character, CharacterSkill skill, int index)
    {
        ImGui.PushID("skill-cell-" + skill.SkillId);
        if (ImGui.BeginTable("skill-cell-inner", 2, ImGuiTableFlags.SizingStretchProp, new Vector2(-1f, 0f)))
        {
            ImGui.TableSetupColumn("skill-info", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("skill-action", ImGuiTableColumnFlags.WidthFixed, 78f);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawSkillIcon(skill, 20f);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{SkillDisplayName(skill)} Lv.{skill.Level}");

            float progress = skill.Level >= 100 ? 1f : Math.Clamp(skill.CurrentExperience / Math.Max(1f, skill.ExperienceRequiredToLevelUp), 0f, 1f);
            ImGui.ProgressBar(progress, new Vector2(-1f, 14f), "");

            ImGui.TableSetColumnIndex(1);
            if (DrawGameButton(T("修改", "Edit") + "##edit", new Vector2(68f, 24f)))
            {
                expandedSkillId = skill.SkillId;
                selectedSkill = index;
                skillLevelSet = Math.Clamp(skill.Level, 0, 100);
                openSkillEditorPopup = true;
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.PopID();
    }

    private static void DrawSkillEditorPopup(PlayerCharacterModel character, List<CharacterSkill> skills)
    {
        if (openSkillEditorPopup)
        {
            ImGui.OpenPopup("skill-editor-popup");
            openSkillEditorPopup = false;
        }

        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.025f, 0.045f, 0.055f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.78f, 0.52f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.18f, 0.10f, 0.06f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.28f, 0.16f, 0.08f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 2f);
        bool popupOpen = ImGui.BeginPopup("skill-editor-popup", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar);
        if (!popupOpen)
        {
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(4);
            return;
        }

        CharacterSkill? skill = skills.FirstOrDefault(s => s.SkillId == expandedSkillId);
        if (skill == null)
        {
            ImGui.TextWrapped(T("技能已经不可用。", "Skill is no longer available."));
            if (DrawGameButton(T("关闭", "Close"), new Vector2(SmallActionButtonWidth, 30f)))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(4);
            return;
        }

        DrawSkillIcon(skill, 28f);
        ImGui.SameLine();
        ImGui.TextUnformatted($"{SkillDisplayName(skill)}  Lv.{skill.Level}");
        float progress = skill.Level >= 100 ? 1f : Math.Clamp(skill.CurrentExperience / Math.Max(1f, skill.ExperienceRequiredToLevelUp), 0f, 1f);
        ImGui.ProgressBar(progress, new Vector2(320f, 16f), $"{skill.CurrentExperience:0}/{skill.ExperienceRequiredToLevelUp:0}");
        ImGui.Separator();

        DrawNumberInput(T("增加经验", "Add XP"), "inline-skill-xp", ref skillExperienceAmount, 1, MaxCommandAmount);
        if (DrawGameButton(T("增加经验", "Add XP"), new Vector2(SmallActionButtonWidth, 30f)))
        {
            status = AddExperienceToSelectedSkill(character, skill, skillExperienceAmount);
        }
        ImGui.SameLine();
        if (DrawGameButton(T("+5 级", "+5 Levels"), new Vector2(SmallActionButtonWidth, 30f)))
        {
            status = AddNativeLevelsToSkill(character, skill, 5);
        }

        DrawNumberInput(T("设置等级", "Set Level"), "inline-skill-level", ref skillLevelSet, 0, 100);
        if (DrawGameButton(T("应用等级", "Apply Level"), new Vector2(SmallActionButtonWidth, 30f)))
        {
            status = SetPlayerSkillLevel(skill.SkillId, skillLevelSet);
        }
        ImGui.SameLine();
        if (DrawGameButton(T("关闭", "Close"), new Vector2(SmallActionButtonWidth, 30f)))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
    }

    private static void DrawSkillAdvancedControls()
    {
        DrawToolSectionHeader(T("高级功能", "Advanced"));
        DrawNumberInput(T("全部等级", "All Levels"), "all-skill-level", ref skillLevelSet, 0, 100);
        if (ImGui.Button(T("设置全部技能等级", "Set All Skill Levels"), new Vector2(ActionButtonWidth, 32f)))
        {
            status = SetAllPlayerSkillLevels(skillLevelSet);
        }

        DrawIntApplyRow(T("经验倍率", "XP Multiplier"), "skill-xp-multiplier", ref skillExperienceMultiplier, 1, 100, SetSkillExperienceMultiplier);
        ImGui.TextDisabled(T(
            "倍率为 1 且没有缓存时不会反复扫描技能表；修改后会刷新角色技能缓存。",
            "At 1x with no cached base values, the trainer does not repeatedly scan skill data; changes refresh character skill caches."));
    }

    private static void DrawSkillIcon(CharacterSkill skill, float size)
    {
        string? iconId = null;
        try
        {
            iconId = SkillsDataBase.GetSkillOrNull(skill.SkillId)?.Icon;
        }
        catch
        {
        }

        DrawIconImage(iconId, size);
    }

    private static string SkillDisplayName(CharacterSkill skill)
    {
        try
        {
            string name = skill.Name;
            if (!string.IsNullOrWhiteSpace(name) && name != skill.SkillId && !name.Contains("*skills:name", StringComparison.OrdinalIgnoreCase))
            {
                return currentLanguage == UiLanguage.English ? CleanInternalId(skill.SkillId) : name;
            }
        }
        catch
        {
        }

        return currentLanguage == UiLanguage.English
            ? CleanInternalId(skill.SkillId)
            : SkillChineseFallback(skill.SkillId);
    }

    private static string SkillChineseFallback(string skillId)
    {
        return skillId switch
        {
            "skill:crossbows" => "弩",
            "skill:swords" => "剑",
            "skill:shields" => "盾",
            "skill:spears" => "矛",
            "skill:woodcutting" => "伐木",
            "skill:mining" => "采矿",
            "skill:construction" => "建造",
            "skill:throwing" => "投掷",
            "skill:unarmed" => "徒手",
            "skill:tomes" => "卷轴",
            "skill:daggers" => "匕首",
            "skill:sledgehammers" => "大锤",
            "skill:bows" => "弓",
            _ => CleanInternalId(skillId)
        };
    }

    private static string AddExperienceToSelectedSkill(PlayerCharacterModel character, CharacterSkill skill, int experience)
    {
        try
        {
            experience = Math.Clamp(experience, 1, MaxCommandAmount);
            SkillsManager.AddExperienceToSkillType(character, skill.SkillId, experience);
            return T($"已给 {SkillDisplayName(skill)} 增加 {experience} 经验。", $"Added {experience} XP to {SkillDisplayName(skill)}.");
        }
        catch (Exception ex)
        {
            return T("增加技能经验失败：", "Failed to add skill XP: ") + ex.Message;
        }
    }

    private static string AddNativeLevelsToSkill(PlayerCharacterModel character, CharacterSkill skill, int levels)
    {
        try
        {
            if (skill.Level >= 100)
            {
                return T("该技能已经满级。", "This skill is already max level.");
            }

            int levelCount = Math.Clamp(Math.Min(levels, 100 - skill.Level), 1, 100);
            float experience = 1f;
            for (int i = 0; i < levelCount; i++)
            {
                experience += skill.ExperienceRequiredToLevelUpAtLevel(skill.Level + i);
            }

            float factor = Math.Max(0.0001f, skill.ExperienceGainFactor);
            SkillsManager.AddExperienceToSkillType(character, skill.SkillId, experience / factor);
            return T($"已按原版逻辑提升 {SkillDisplayName(skill)} {levelCount} 级。", $"Raised {SkillDisplayName(skill)} by {levelCount} native levels.");
        }
        catch (Exception ex)
        {
            return T("原版升级失败：", "Native level-up failed: ") + ex.Message;
        }
    }

    private static string SetPlayerSkillLevel(string skillId, int level)
    {
        try
        {
            level = Math.Clamp(level, 0, 100);
            bool changed = false;
            if (TrySetSkillLevel(GetLocalPlayerCharacter(), skillId, level))
            {
                changed = true;
            }
            if (TrySetSkillLevel(ServerGlobals.CurrentPlayerCharacter?.Character, skillId, level))
            {
                changed = true;
            }
            if (!changed)
            {
                return T("没有找到该技能。", "Skill was not found.");
            }

            SkillsService.Send_PlayerSkillsChanged(new PlayerSkillsChangedMessage
            {
                SkillId = skillId,
                CurrentSkillLevel = level
            });
            return T($"已设置 {SkillChineseFallback(skillId)} 等级为 {level}。", $"Set {CleanInternalId(skillId)} level to {level}.");
        }
        catch (Exception ex)
        {
            return T("设置技能等级失败：", "Failed to set skill level: ") + ex.Message;
        }
    }

    private static string SetAllPlayerSkillLevels(int level)
    {
        try
        {
            PlayerCharacterModel? character = GetLocalPlayerCharacter();
            if (character == null)
            {
                return T("玩家角色尚未就绪。", "Player character is not ready.");
            }

            level = Math.Clamp(level, 0, 100);
            foreach (string skillId in character.Skills.CharacterSkills.Keys.ToList())
            {
                TrySetSkillLevel(character, skillId, level);
                TrySetSkillLevel(ServerGlobals.CurrentPlayerCharacter?.Character, skillId, level);
                SkillsService.Send_PlayerSkillsChanged(new PlayerSkillsChangedMessage
                {
                    SkillId = skillId,
                    CurrentSkillLevel = level
                });
            }
            return T($"已设置全部技能等级为 {level}。", $"Set all skill levels to {level}.");
        }
        catch (Exception ex)
        {
            return T("设置全部技能等级失败：", "Failed to set all skill levels: ") + ex.Message;
        }
    }

    private static bool TrySetSkillLevel(PlayerCharacterModel? character, string skillId, int level)
    {
        if (character?.Skills.CharacterSkills.TryGetValue(skillId, out CharacterSkill? skill) != true || skill == null)
        {
            return false;
        }

        skill.Level = level;
        skill.CurrentExperience = 0f;
        return true;
    }

    private static string SetSkillExperienceMultiplier(int value)
    {
        skillExperienceMultiplier = Math.Clamp(value, 1, 100);
        ApplySkillExperienceMultiplier();
        return T($"已设置技能经验倍率：{skillExperienceMultiplier} 倍。", $"Set skill XP multiplier: {skillExperienceMultiplier}x.");
    }

    private static string SetFavourPoints(int amount)
    {
        try
        {
            PlayerCharacterModel? character = GetLocalPlayerCharacter();
            if (character == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            amount = Math.Clamp(amount, 0, 999999);
            if (ServerGlobals.CurrentPlayerCharacter?.Character != null)
            {
                ServerGlobals.CurrentPlayerCharacter.Character.Skills.CurrentFavourPoints = amount;
            }
            FavourManager.SynchUpdateFavoursAndCurrentFavourPoints(character.EntityId, Array.Empty<(string, int)>(), amount);
            character.Skills.CurrentFavourPoints = amount;
            favourPoints = amount;
            favourPointsInitialized = true;
            return T("已设置恩惠点数。", "Favour points set.");
        }
        catch (Exception ex)
        {
            return T("设置恩惠点失败：", "Failed to set favour points: ") + ex.Message;
        }
    }

    private static string SetWorshipPoints(int amount)
    {
        try
        {
            amount = Math.Clamp(amount, 0, 999999);
            ServerGameState.WorshipPoints = amount;
            GameState.WorshipPoints = amount;
            worshipPoints = amount;
            return T("已设置崇拜点数。", "Worship points set.");
        }
        catch (Exception ex)
        {
            return T("设置崇拜点失败：", "Failed to set worship points: ") + ex.Message;
        }
    }

    private static string SavePosition(int slot)
    {
        try
        {
            if (Globals.Game?.Player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            slot = Math.Clamp(slot, 1, 9);
            savedPositions[slot] = Globals.Game.Player.Position2;
            return T($"已保存位置到槽位 {slot}。", $"Saved position to slot {slot}.");
        }
        catch (Exception ex)
        {
            return T("保存位置失败：", "Failed to save position: ") + ex.Message;
        }
    }

    private static string LoadPosition(int slot)
    {
        try
        {
            slot = Math.Clamp(slot, 1, 9);
            if (!savedPositions.TryGetValue(slot, out Microsoft.Xna.Framework.Vector2 position))
            {
                return T($"槽位 {slot} 还没有保存位置。", $"Slot {slot} has no saved position.");
            }
            if (Globals.Game?.Player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            Globals.Game.Player.Position2 = position;
            Globals.Game.Camera.JumpToTarget();
            return T($"已读取槽位 {slot} 的位置。", $"Loaded position from slot {slot}.");
        }
        catch (Exception ex)
        {
            return T("读取位置失败：", "Failed to load position: ") + ex.Message;
        }
    }

    private static string CopyCurrentPositionToManualFields()
    {
        try
        {
            if (Globals.Game?.Player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            Microsoft.Xna.Framework.Vector2 position = Globals.Game.Player.Position2;
            manualPositionX = position.X;
            manualPositionY = position.Y;
            return T("已读取当前位置。", "Current position copied.");
        }
        catch (Exception ex)
        {
            return T("读取当前位置失败：", "Failed to copy current position: ") + ex.Message;
        }
    }

    private static string TeleportToManualPosition()
    {
        try
        {
            if (Globals.Game?.Player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            Globals.Game.Player.Position2 = new Microsoft.Xna.Framework.Vector2(manualPositionX, manualPositionY);
            Globals.Game.Camera.JumpToTarget();
            return T($"已传送到坐标 X {manualPositionX:0.0}, Y {manualPositionY:0.0}。", $"Teleported to X {manualPositionX:0.0}, Y {manualPositionY:0.0}.");
        }
        catch (Exception ex)
        {
            return T("手动传送失败：", "Manual teleport failed: ") + ex.Message;
        }
    }

    private static void HandleMapTeleportInput()
    {
        const int rightMouseButton = 2;
        if (!mapTeleportEnabled)
        {
            NativeInput.IsPressedOnce(rightMouseButton, ref mapTeleportRightWasDown);
            return;
        }

        bool hasCurrentMapTile = TryGetMapPointerTile(out Microsoft.Xna.Framework.Vector2 tile);
        if (hasCurrentMapTile)
        {
            recordedMapTeleportTile = tile;
            recordedMapTeleportTime = DateTime.Now;
        }
        else
        {
            recordedMapTeleportTile = null;
        }

        if (NativeInput.IsPressedOnce(rightMouseButton, ref mapTeleportRightWasDown) && hasCurrentMapTile)
        {
            status = TeleportToMapTile(tile);
        }
    }

    private static bool TryGetMapPointerTile(out Microsoft.Xna.Framework.Vector2 tile)
    {
        tile = default;
        try
        {
            if (Globals.Game?.Player == null || GameState.WorldTiles == null)
            {
                return false;
            }

            WorldOverviewWindow? overviewWindow = PlayerUi.WorldOverviewWindow;
            if (overviewWindow != null &&
                ((CandideUiElement)overviewWindow).Visible &&
                TryGetWorldOverviewCursorTile(overviewWindow, out tile))
            {
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetWorldOverviewCursorTile(WorldOverviewWindow overviewWindow, out Microsoft.Xna.Framework.Vector2 tile)
    {
        tile = default;
        try
        {
            object? map = overviewWindow.GetType().GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(overviewWindow);
            object? value = map?.GetType().GetField("CursorTilePosition", BindingFlags.Instance | BindingFlags.Public)?.GetValue(map);
            if (value is not Microsoft.Xna.Framework.Vector2 cursorTile)
            {
                return false;
            }

            tile = cursorTile;
            ClampMapTile(ref tile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ClampMapTile(ref Microsoft.Xna.Framework.Vector2 tile)
    {
        if (GameState.WorldTiles == null)
        {
            return;
        }

        tile.X = Math.Clamp(tile.X, 0f, GameState.WorldTiles.GetLength(0) - 1);
        tile.Y = Math.Clamp(tile.Y, 0f, GameState.WorldTiles.GetLength(1) - 1);
    }

    private static string TeleportToRecordedMapPointer()
    {
        if (!recordedMapTeleportTile.HasValue && !TryGetMapPointerTile(out _))
        {
            return T("请先打开世界地图，并把鼠标移动到目标位置。", "Open the world map and move the cursor to a target first.");
        }

        if (TryGetMapPointerTile(out Microsoft.Xna.Framework.Vector2 tile))
        {
            recordedMapTeleportTile = tile;
            recordedMapTeleportTime = DateTime.Now;
        }

        return recordedMapTeleportTile.HasValue
            ? TeleportToMapTile(recordedMapTeleportTile.Value)
            : T("没有识别到地图鼠标位置。", "Map cursor position was not detected.");
    }

    private static string TeleportToMapTile(Microsoft.Xna.Framework.Vector2 tile)
    {
        try
        {
            if (Globals.Game?.Player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            Microsoft.Xna.Framework.Vector3 position = new(tile.X * 16f, tile.Y * 16f, Globals.Game.Player.Position.Z);
            manualPositionX = position.X;
            manualPositionY = position.Y;
            if (!WorldManager.MovePlayerToWorldAndPosition(GameState.OutsideWorldId, position))
            {
                return T("传送失败：无法移动到目标世界。", "Teleport failed: could not move to target world.");
            }

            Globals.Game.Camera.JumpToTarget();
            CloseWorldMapWindow();
            return T($"已传送到地图位置：{(int)tile.X}, {(int)tile.Y}。", $"Teleported to map tile: {(int)tile.X}, {(int)tile.Y}.");
        }
        catch (Exception ex)
        {
            return T("地图瞬移失败：", "Map teleport failed: ") + ex.Message;
        }
    }

    private static void CloseWorldMapWindow()
    {
        try
        {
            WorldOverviewWindow? worldOverviewWindow = PlayerUi.WorldOverviewWindow;
            if (worldOverviewWindow != null)
            {
                worldOverviewWindow.Close();
            }
        }
        catch
        {
        }
    }

    private static string OpenWorldMapFromTrainer()
    {
        try
        {
            PlayerUi.ShowWorldMap();
            return T("已尝试打开世界地图。", "Tried to open the world map.");
        }
        catch (Exception ex)
        {
            return T("打开世界地图失败：", "Failed to open world map: ") + ex.Message;
        }
    }

    private static string RevealFullMap()
    {
        try
        {
            if (GameState.WorldTiles == null || GameState.TileHeights == null || GameState.MappedTiles == null)
            {
                return T("世界地图尚未加载。", "World map is not loaded yet.");
            }

            int width = GameState.WorldTiles.GetLength(0);
            int height = GameState.WorldTiles.GetLength(1);
            if (ServerGameState.WorldTiles != null && ServerRunState.TileHeights != null &&
                ServerGameState.WorldTiles.GetLength(0) >= width &&
                ServerGameState.WorldTiles.GetLength(1) >= height &&
                ServerRunState.TileHeights.GetLength(0) >= width &&
                ServerRunState.TileHeights.GetLength(1) >= height)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        GameState.WorldTiles[x, y] = ServerGameState.WorldTiles[x, y];
                        GameState.TileHeights[x, y] = ServerRunState.TileHeights[x, y];
                    }
                }
            }

            int mappedWidth = GameState.MappedTiles.GetLength(0);
            int mappedHeight = GameState.MappedTiles.GetLength(1);
            for (int x = 0; x < mappedWidth; x++)
            {
                for (int y = 0; y < mappedHeight; y++)
                {
                    GameState.MappedTiles[x, y] = true;
                }
            }

            ExteriorWorldHandler.WorldMap?.UpdateWholeRectangle(0, 0, width, height);
            ExteriorWorldHandler.FogOfWarWorldMap?.UpdateWholeRectangle(0, 0, mappedWidth, mappedHeight);
            ExteriorWorldHandler.FogOfWarWorldMap?.QueueUpdateWholeMap();
            return T("已揭开世界地图迷雾。", "World map fog revealed.");
        }
        catch (Exception ex)
        {
            return T("开图失败：", "Reveal map failed: ") + ex.Message;
        }
    }

    private static void ApplySkillExperienceMultiplier()
    {
        try
        {
            if ((skillExperienceMultiplier <= 1 && baseSkillExperienceGainFactors.Count == 0) || SkillsDataBase.DataMap.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, SkillData> item in SkillsDataBase.DataMap.ToList())
            {
                if (!baseSkillExperienceGainFactors.TryGetValue(item.Key, out float baseValue))
                {
                    baseValue = item.Value.ExperienceGainFactor;
                    baseSkillExperienceGainFactors[item.Key] = baseValue;
                }

                SkillData updated = item.Value;
                updated.ExperienceGainFactor = baseValue * skillExperienceMultiplier;
                SkillsDataBase.DataMap[item.Key] = updated;
            }

            RefreshCharacterSkillDataCache(GetLocalPlayerCharacter()?.Skills.CharacterSkills);
            RefreshCharacterSkillDataCache(ServerGlobals.CurrentPlayerCharacter?.Character?.Skills.CharacterSkills);
        }
        catch
        {
        }
    }

    private static void RefreshCharacterSkillDataCache(Dictionary<string, CharacterSkill>? skills)
    {
        if (skills == null || characterSkillDataField == null)
        {
            return;
        }

        foreach (CharacterSkill skill in skills.Values)
        {
            if (SkillsDataBase.DataMap.TryGetValue(skill.SkillId, out SkillData data))
            {
                characterSkillDataField.SetValue(skill, data);
            }
        }
    }

    private static void DrawPageTitle(string title)
    {
        ImGui.TextColored(new Vector4(1f, 0.94f, 0.72f, 1f), title);
        ImGui.Separator();
    }

    private static void DrawToolSectionHeader(string title)
    {
        ImGui.TextColored(new Vector4(0.9f, 0.78f, 0.48f, 1f), title);
    }

    private static bool DrawGameButton(string label, Vector2 size)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.38f, 0.21f, 0.11f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.54f, 0.33f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.68f, 0.48f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.94f, 0.84f, 0.62f, 1f));
        bool clicked = ImGui.Button(label, size);
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.86f, 0.62f, 0.28f, 1f)), 2f);
        drawList.AddRect(min + new Vector2(1f, 1f), max - new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0.16f, 0.08f, 0.04f, 1f)), 1f);
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(3);
        return clicked;
    }

    private static void DrawFormLabel(string label)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(FormLabelWidth);
    }

    private static void DrawTextInput(string label, string id, ref string value, uint maxLength)
    {
        DrawFormLabel(label);
        ImGui.SetNextItemWidth(SearchInputWidth);
        ImGui.InputText("##" + id, ref value, maxLength);
    }

    private static bool DrawSearchInput(string label, string id, ref string value)
    {
        bool changed = false;

        DrawFormLabel(label);
        ImGui.SetNextItemWidth(SearchInputWidth);
        if (ImGui.InputText("##" + id, ref value, 80u))
        {
            changed = true;
        }
        return changed;
    }

    private static void DrawNumberInput(string label, string id, ref int value, int min, int max)
    {
        DrawFormLabel(label);
        ImGui.SetNextItemWidth(SmallNumberWidth);
        if (ImGui.InputInt("##" + id, ref value))
        {
            value = Math.Clamp(value, min, max);
        }
        value = Math.Clamp(value, min, max);
    }

    private static bool DrawCheckboxRow(string label, ref bool value)
    {
        ImGui.PushID(label);
        DrawFormLabel(label);
        bool changed = ImGui.Checkbox("##value", ref value);
        ImGui.SameLine();
        ImGui.TextDisabled(value ? T("已开启", "On") : T("已关闭", "Off"));
        ImGui.PopID();
        return changed;
    }

    private static void DrawIntApplyRow(string label, string id, ref int value, int min, int max, Func<int, string> apply)
    {
        ImGui.PushID(id);
        DrawFormLabel(label);
        ImGui.SetNextItemWidth(SmallNumberWidth);
        if (ImGui.InputInt("##value", ref value))
        {
            value = Math.Clamp(value, min, max);
        }
        value = Math.Clamp(value, min, max);
        ImGui.SameLine();
        if (ImGui.Button(T("应用", "Apply"), new Vector2(SmallActionButtonWidth, 30f)))
        {
            status = apply(value);
        }
        ImGui.PopID();
    }

    private static void DrawSustainedPlayerIntRow(
        string label,
        string id,
        ref int value,
        int min,
        int max,
        Func<int, string> apply,
        ref bool keepEnabled,
        Func<string> enableKeep,
        Func<string> disableKeep)
    {
        ImGui.PushID(id);
        DrawFormLabel(label);
        ImGui.SetNextItemWidth(SmallNumberWidth);
        if (ImGui.InputInt("##value", ref value))
        {
            value = Math.Clamp(value, min, max);
        }

        value = Math.Clamp(value, min, max);
        ImGui.SameLine();
        if (keepEnabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.45f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.72f, 0.52f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.94f, 0.72f, 1f));
            if (ImGui.Button(T("关闭并恢复", "Off + Restore"), new Vector2(132f, 30f)))
            {
                keepEnabled = false;
                status = disableKeep();
            }

            ImGui.PopStyleColor(3);
        }
        else if (ImGui.Button(T("应用", "Apply"), new Vector2(132f, 30f)))
        {
            keepEnabled = true;
            status = enableKeep();
            if (keepEnabled)
            {
                status = $"{status} {apply(value)}";
            }
        }

        ImGui.PopID();
    }

    private static void DrawItemSelector(string label, string id, List<CatalogItem> items, ref int selectedIndex, Func<CatalogItem, string> display)
    {
        selectedIndex = Math.Clamp(selectedIndex, 0, items.Count - 1);
        CatalogItem current = items[selectedIndex];
        DrawFormLabel(label);
        DrawItemIcon(current, 24f);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##" + id, display(current)))
        {
            for (int i = 0; i < items.Count; i++)
            {
                bool selected = i == selectedIndex;
                DrawItemIcon(items[i], 20f);
                ImGui.SameLine();
                if (ImGui.Selectable(display(items[i]), selected))
                {
                    selectedIndex = i;
                    ImGui.CloseCurrentPopup();
                }
                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }
    }

    private static bool DrawItemCategorySelector(string label, string id, ref int selectedCategory)
    {
        selectedCategory = Math.Clamp(selectedCategory, 0, ItemCategories.Length - 1);
        string preview = T(ItemCategories[selectedCategory].Cn, ItemCategories[selectedCategory].En);
        bool changed = false;
        DrawFormLabel(label);
        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##" + id, preview))
        {
            for (int i = 0; i < ItemCategories.Length; i++)
            {
                bool selected = i == selectedCategory;
                if (ImGui.Selectable(T(ItemCategories[i].Cn, ItemCategories[i].En), selected))
                {
                    selectedCategory = i;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private static void DrawCommandInputRow(string label, string command, ref string value)
    {
        ImGui.PushID(label);
        DrawFormLabel(label);
        ImGui.SetNextItemWidth(SearchInputWidth);
        ImGui.InputText("##command-value", ref value, 120u);
        ImGui.SameLine();
        if (ImGui.Button(T("执行", "Run"), new Vector2(SmallActionButtonWidth, 30f)))
        {
            string argument = value.Trim();
            status = string.IsNullOrWhiteSpace(argument)
                ? T($"请输入 {label} ID。", $"Enter {label} ID.")
                : RunCommand($"{command} {argument}");
        }
        ImGui.PopID();
    }

    private static void DrawIdDropdownCommand(
        string searchLabel,
        string comboLabel,
        string buttonLabel,
        string searchInputId,
        ref string search,
        ref int selectedIndex,
        Func<IEnumerable<string>> idSource,
        Func<string, string> displayName,
        string command,
        Func<string, string?>? iconId = null,
        Func<string, float, bool>? drawIcon = null,
        bool enableMousePlacement = false)
    {
        ImGui.PushID(comboLabel);
        if (DrawSearchInput(searchLabel, searchInputId, ref search))
        {
            selectedIndex = 0;
        }

        if (!TryGetCachedIdDropdownEntries(comboLabel, out List<IdDropdownEntry>? cachedEntries) &&
            !staticDropdownPrewarmCompleted)
        {
            DrawAnimatedLoadingText(T("列表正在加载，请稍候", "Loading list. Please wait"));
            ImGui.PopID();
            return;
        }

        List<IdDropdownEntry> entries = FilterIdDropdownEntries(
            cachedEntries ?? GetIdDropdownEntries(comboLabel, idSource, displayName, iconId),
            search).Take(300).ToList();
        if (entries.Count == 0)
        {
            ImGui.TextWrapped(T("没有匹配项。", "No matching entries."));
            ImGui.PopID();
            return;
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, entries.Count - 1);
        IdDropdownEntry selectedEntry = entries[selectedIndex];
        DrawFormLabel(comboLabel);
        if (DrawDropdownIcon(selectedEntry, 24f, drawIcon))
        {
            ImGui.SameLine();
        }
        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##combo", selectedEntry.DisplayName))
        {
            for (int i = 0; i < entries.Count; i++)
            {
                bool selected = i == selectedIndex;
                if (DrawDropdownIcon(entries[i], 20f, drawIcon))
                {
                    ImGui.SameLine();
                }
                if (ImGui.Selectable(BuildSelectableId(entries[i].DisplayName, entries[i].Id, i), selected))
                {
                    selectedIndex = i;
                    ImGui.CloseCurrentPopup();
                }
                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        DrawFormLabel("");
        if (ImGui.Button(buttonLabel, new Vector2(ActionButtonWidth, 32f)))
        {
            status = RunCommand($"{command} {entries[selectedIndex].Id}");
        }
        if (enableMousePlacement)
        {
            ImGui.SameLine();
            if (DrawGameButton(T("鼠标放置", "Place By Mouse"), new Vector2(ActionButtonWidth, 32f)))
            {
                status = StartMouseEntityPlacement(entries[selectedIndex]);
            }
        }

        ImGui.PopID();
    }

    private static string BuildSelectableId(string displayName, string id, int index)
    {
        return $"{displayName}##{id}-{index}";
    }

    private static void DrawAnimatedLoadingText(string message)
    {
        ImGui.TextColored(LoadingTextColor, message + GetLoadingDots());
    }

    private static string GetLoadingDots()
    {
        int dotCount = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond / 320 % 7) + 1;
        return new string('.', dotCount);
    }

    private static void PrewarmCatalogCache()
    {
        TryEnsureCatalogLoaded(allowBuild: true);
    }

    private static bool TryEnsureCatalogLoaded(bool allowBuild)
    {
        if (catalogLoaded)
        {
            return true;
        }

        if (ItemDataBase.DataMap.Count == 0)
        {
            return false;
        }

        if (!allowBuild)
        {
            return false;
        }

        EnsureCatalogLoaded();
        return catalogLoaded && catalogItems.Count > 0;
    }

    private static void PrewarmStaticDropdownCaches()
    {
        if (staticDropdownPrewarmCompleted)
        {
            return;
        }

        EnsureStaticDropdownPrewarmQueue();
        if (staticDropdownPrewarmQueue.Count == 0)
        {
            staticDropdownPrewarmCompleted = true;
            return;
        }

        Func<bool>? prewarmTask = null;
        try
        {
            prewarmTask = staticDropdownPrewarmQueue.Dequeue();
            bool warmed = prewarmTask.Invoke();
            if (warmed)
            {
                if (staticDropdownPrewarmQueue.Count == 0)
                {
                    staticDropdownPrewarmCompleted = true;
                }
            }
            else
            {
                staticDropdownPrewarmQueue.Enqueue(prewarmTask);
            }
        }
        catch
        {
            // Databases can still be settling during early save load; retry on the next tick.
            if (prewarmTask != null)
            {
                staticDropdownPrewarmQueue.Enqueue(prewarmTask);
            }
        }
    }

    private static void EnsureStaticDropdownPrewarmQueue()
    {
        if (staticDropdownPrewarmCompleted)
        {
            return;
        }

        if (staticDropdownPrewarmLanguage != currentLanguage)
        {
            staticDropdownPrewarmQueue.Clear();
            staticDropdownPrewarmLanguage = currentLanguage;
        }

        if (staticDropdownPrewarmQueue.Count > 0)
        {
            return;
        }

        staticDropdownPrewarmQueue.Enqueue(() => PrewarmIdDropdownEntries(
            T("建筑列表", "Construction List"),
            GetConstructionIds,
            ConstructionDisplayName,
            ConstructionIconId));
        staticDropdownPrewarmQueue.Enqueue(() => PrewarmIdDropdownEntries(
            T("Boss 列表", "Boss List"),
            GetBossIds,
            BossDisplayName,
            BossIconId));
        staticDropdownPrewarmQueue.Enqueue(() => PrewarmIdDropdownEntries(
            T("袭击列表", "Raid List"),
            GetRaidIds,
            RaidDisplayName,
            null));
        staticDropdownPrewarmQueue.Enqueue(() => PrewarmIdDropdownEntries(
            T("建筑列表", "Construction List"),
            GetConstructionIds,
            ConstructionDisplayName,
            ConstructionIconId));
        staticDropdownPrewarmQueue.Enqueue(() => PrewarmIdDropdownEntries(
            "item-aura-list",
            static () => ItemAuraDataBase.AuraDataMap.Keys,
            ItemAuraDisplayName,
            null));
        staticDropdownPrewarmQueue.Enqueue(() => PrewarmIdDropdownEntries(
            "citizen-aura-list",
            static () => CitizenAuraDatabase.DataMap.Values
                .Where(aura => aura.Type == CitizenAuraType.Trait)
                .Select(aura => aura.Id),
            AuraDisplayName,
            null));
        staticDropdownPrewarmQueue.Enqueue(() => PrewarmIdDropdownEntries(
            T("实体列表", "Entity List"),
            GetSpawnEntityIds,
            EntityDisplayName,
            EntityIconId));
    }

    private static bool PrewarmIdDropdownEntries(
        string cacheKey,
        Func<IEnumerable<string>> idSource,
        Func<string, string> displayName,
        Func<string, string?>? iconId)
    {
        return GetIdDropdownEntries(cacheKey, idSource, displayName, iconId).Count > 0;
    }

    private static List<IdDropdownEntry> GetIdDropdownEntries(
        string cacheKey,
        Func<IEnumerable<string>> idSource,
        Func<string, string> displayName,
        Func<string, string?>? iconId)
    {
        string fullKey = GetIdDropdownCacheKey(cacheKey);
        if (idDropdownCache.TryGetValue(fullKey, out List<IdDropdownEntry>? cached))
        {
            return cached;
        }

        List<IdDropdownEntry> entries = idSource()
            .Select(id =>
            {
                string name = displayName(id);
                string cleanId = CleanInternalId(id);
                string? icon = iconId?.Invoke(id);
                string searchText = string.Join(" ", id, cleanId, name);
                return new IdDropdownEntry(id, name, searchText, icon);
            })
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count > 0)
        {
            idDropdownCache[fullKey] = entries;
        }
        return entries;
    }

    private static string GetIdDropdownCacheKey(string cacheKey)
    {
        string languageKey = currentLanguage == UiLanguage.English ? "en" : "cn";
        return $"{languageKey}|{cacheKey}";
    }

    private static bool TryGetCachedIdDropdownEntries(string cacheKey, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<IdDropdownEntry>? entries)
    {
        return idDropdownCache.TryGetValue(GetIdDropdownCacheKey(cacheKey), out entries);
    }

    private static IEnumerable<IdDropdownEntry> FilterIdDropdownEntries(IEnumerable<IdDropdownEntry> entries, string search)
    {
        string term = search.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            return entries;
        }

        return entries.Where(entry => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool DrawDropdownIcon(IdDropdownEntry entry, float size, Func<string, float, bool>? drawIcon)
    {
        if (DrawOptionalIconImage(entry.IconId, size))
        {
            return true;
        }

        return drawIcon is not null && drawIcon(entry.Id, size);
    }

    private static IEnumerable<string> FilterIds(IEnumerable<string> ids, string search, Func<string, string> displayName)
    {
        string term = search.Trim();
        IEnumerable<string> query = ids;
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(id =>
                id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                CleanInternalId(id).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                displayName(id).Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return query.OrderBy(displayName);
    }

    private static IEnumerable<string> GetSpawnEntityIds()
    {
        return DoodadDatabaseManager.Names.Keys;
    }

    private static IEnumerable<string> GetConstructionIds()
    {
        return ConstructionDataBase.DataMap.Keys;
    }

    private static IEnumerable<string> GetBossIds()
    {
        return BossDataBase.DataMap.Keys;
    }

    private static IEnumerable<string> GetRaidIds()
    {
        return RaidDataBase.DataMap.Keys;
    }

    private static string ConstructionDisplayName(string id)
    {
        return CleanInternalId(id);
    }

    private static string EntityDisplayName(string id)
    {
        try
        {
            string translatedName = EntityTranslatedDisplayName(id);
            if (!string.IsNullOrWhiteSpace(translatedName))
            {
                return translatedName;
            }

            if (TryGetEntityBaseGuid(id, out Guid baseGuid) &&
                DoodadDatabaseManager.TryGetEntityBaseData(baseGuid, out EntityWrapper? entity) &&
                !string.IsNullOrWhiteSpace(entity.Name))
            {
                return EntityFallbackDisplayName(entity.Name);
            }
        }
        catch
        {
        }

        return EntityFallbackDisplayName(id);
    }

    private static string EntityTranslatedDisplayName(string id)
    {
        return "";
    }

    private static string EntityFallbackDisplayName(string rawName)
    {
#if NEXUS_FREE
        return CleanInternalId(rawName);
#else
        if (currentLanguage == UiLanguage.English)
        {
            return CleanInternalId(rawName);
        }

        string key = NormalizeEntityNameKey(rawName);
        if (entityNameOverrides.TryGetValue(key, out string? overrideName))
        {
            return overrideName;
        }

        string[] parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return CleanInternalId(rawName);
        }

        bool translatedAny = false;
        List<string> displayParts = new(parts.Length);
        foreach (string part in parts)
        {
            if (entityWordNames.TryGetValue(part, out string? translated))
            {
                displayParts.Add(translated);
                translatedAny = true;
            }
            else
            {
                displayParts.Add(char.IsDigit(part[0]) ? part : "实体");
            }
        }

        return translatedAny ? string.Join("", displayParts) : "未译实体";
#endif
    }

    private static string NormalizeEntityNameKey(string rawName)
    {
        StringBuilder builder = new(rawName.Length);
        bool previousSeparator = false;
        foreach (char c in rawName.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                previousSeparator = false;
                continue;
            }

            if (!previousSeparator)
            {
                builder.Append('_');
                previousSeparator = true;
            }
        }

        return builder.ToString().Trim('_');
    }

    private static string? EntityIconId(string id)
    {
        try
        {
            if (!TryGetEntityBaseGuid(id, out Guid baseGuid))
            {
                return null;
            }

            string? iconId = BossDataBase.DataMap.Values.FirstOrDefault(boss => boss.BossEntityBaseId == baseGuid)?.IconId;
            if (IsValidIconId(iconId))
            {
                return iconId;
            }

            iconId = ConstructionDataBase.DataMap.Values.FirstOrDefault(construction =>
                Guid.TryParse(construction.SpawnedId, out Guid spawnedId) && spawnedId == baseGuid)?.IconId;
            if (IsValidIconId(iconId))
            {
                return iconId;
            }

            iconId = ConstructionResourcesDataBase.DataMap.Values.FirstOrDefault(resource =>
                resource.DefaultBaseGuid.HasValue && resource.DefaultBaseGuid.Value == baseGuid).Icon;
            if (IsValidIconId(iconId))
            {
                return iconId;
            }

            iconId = ItemDataBase.DataMap.Values.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Id, EntityDisplayName(id), StringComparison.OrdinalIgnoreCase))?.Icon;
            if (IsValidIconId(iconId))
            {
                return iconId;
            }

            if (FurnitureDataBase.TryGetFurnitureIdFromEntity(baseGuid, null, out string? furnitureId))
            {
                iconId = ItemDataBase.GetItemDataOrNull(furnitureId)?.Icon;
                if (IsValidIconId(iconId))
                {
                    return iconId;
                }
            }

            string[] candidates =
            {
                id,
                id.Replace(" ", "_", StringComparison.Ordinal),
                id.Replace(" ", ":", StringComparison.Ordinal),
                EntityDisplayName(id).Replace(" ", "_", StringComparison.Ordinal).ToLowerInvariant()
            };
            foreach (string candidate in candidates)
            {
                if (IsValidIconId(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryGetEntityBaseGuid(string id, out Guid baseGuid)
    {
        return Guid.TryParse(id, out baseGuid) || DoodadDatabaseManager.Names.TryGetValue(id, out baseGuid);
    }

    private static bool IsValidIconId(string? iconId)
    {
        if (string.IsNullOrWhiteSpace(iconId))
        {
            return false;
        }

        try
        {
            return IconDataBase.GetIconOrNull(iconId).HasValue;
        }
        catch
        {
            return false;
        }
    }

    private static string? ConstructionIconId(string id)
    {
        try
        {
            return ConstructionDataBase.GetConstructionOrNull(id)?.IconId;
        }
        catch
        {
            return null;
        }
    }

    private static string BossDisplayName(string id)
    {
        return CleanInternalId(id);
    }

    private static string? BossIconId(string id)
    {
        try
        {
            return BossDataBase.GetBossOrNull(id)?.IconId;
        }
        catch
        {
            return null;
        }
    }

    private static string RaidDisplayName(string id)
    {
        return CleanInternalId(id);
    }

    private static string CleanInternalId(string id)
    {
        string cleaned = id.Replace('_', ' ').Replace('-', ' ').Replace(':', ' ');
        return string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static void EnsureCatalogLoaded()
    {
        if (catalogLoaded || catalogLoading)
        {
            return;
        }

        if (ItemDataBase.DataMap.Count == 0)
        {
            return;
        }

        catalogLoading = true;
        try
        {
            catalogItems.Clear();
            foreach (string path in GetCatalogCandidatePaths())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (string line in File.ReadLines(path))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0]))
                    {
                        continue;
                    }

                    string itemId = parts[0].Trim();
                    ItemData? itemData = ItemDataBase.GetItemDataOrNull(itemId);
                    catalogItems.Add(new CatalogItem(
                        itemId,
                        parts.Length > 1 ? parts[1].Trim() : "",
                        parts.Length > 2 ? parts[2].Trim() : itemId,
                        itemData?.Icon,
                        GetItemCategoryId(itemData)));
                }
                status = $"宸插姞杞界墿鍝佺洰褰曪細{path}";
                break;
            }

            ApplyItemFilter();
            ApplyGeneratorFilter();
            catalogLoaded = true;
            if (catalogItems.Count == 0)
            {
                status = T("没有找到 item_catalog.tsv，可以使用物品 ID 直接添加。", "item_catalog.tsv was not found. You can still add items by ID.");
            }
        }
        finally
        {
            catalogLoading = false;
        }
    }

    private static IEnumerable<string> GetCatalogCandidatePaths()
    {
        string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        yield return Path.Combine(pluginDir, "item_catalog.tsv");
        yield return @"C:\SteamLibrary\steamapps\common\romestead\Mods\RomesteadTrainerMod\item_catalog.tsv";
        yield return @"C:\Users\Administrator\Desktop\MODS\RRC_Combined_V1.6.74_CartUnloadFix\RomesteadTrainerMod\item_catalog.tsv";
    }

    private static void ApplyItemFilter()
    {
        string term = itemSearch.Trim();
        filteredItems.Clear();
        IEnumerable<CatalogItem> query = catalogItems;
        string categoryId = ItemCategories[Math.Clamp(selectedItemCategory, 0, ItemCategories.Length - 1)].Id;
        if (categoryId != "all")
        {
            query = query.Where(item => item.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(item =>
                item.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.EnglishName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        filteredItems.AddRange(query.Take(MaxVisibleCatalogItems));
    }

    private static void ApplyGeneratorFilter()
    {
        string term = generatorSearch.Trim();
        filteredGeneratorItems.Clear();
        IEnumerable<CatalogItem> query = catalogItems;
        string categoryId = ItemCategories[Math.Clamp(selectedGeneratorCategory, 0, ItemCategories.Length - 1)].Id;
        if (categoryId != "all")
        {
            query = query.Where(item => item.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(item =>
                item.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.EnglishName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        filteredGeneratorItems.AddRange(query.Take(MaxVisibleCatalogItems));
    }

    private static string GetItemCategoryId(ItemData? itemData)
    {
        if (itemData == null)
        {
            return "other";
        }

        if (itemData.Flags.HasFlag(ItemFlag.Money))
        {
            return "money";
        }

        if (itemData.Flags.HasFlag(ItemFlag.Quest))
        {
            return "quest";
        }

        if (itemData.Flags.HasFlag(ItemFlag.Offering))
        {
            return "offering";
        }

        if (itemData.Flags.HasFlag(ItemFlag.Seed))
        {
            return "seed";
        }

        if (itemData.Flags.HasFlag(ItemFlag.Food))
        {
            return "food";
        }

        if (itemData.Flags.HasFlag(ItemFlag.Ammunition) ||
            itemData.Equippable?.EquipmentType == EquipmentType.Ammunition)
        {
            return "ammunition";
        }

        if (itemData.Equippable != null)
        {
            return itemData.Equippable.EquipmentType switch
            {
                EquipmentType.Weapon or EquipmentType.Offhand or EquipmentType.LumberAxe or EquipmentType.Pickaxe or EquipmentType.FishingRod => "weapon",
                EquipmentType.Helmet or EquipmentType.Armor or EquipmentType.Boots or EquipmentType.Trinket or EquipmentType.Back or EquipmentType.LightSource => "armor",
                _ => "equipment"
            };
        }

        if (itemData.Usable != null || itemData.CitizenConsumable != null || itemData.Fuel != null)
        {
            return "usable";
        }

        if (itemData.Flags.HasFlag(ItemFlag.Material) || itemData.Flags.HasFlag(ItemFlag.Millable))
        {
            return "material";
        }

        return "other";
    }

    private static string GetCatalogDisplayName(CatalogItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.EnglishName))
        {
            return item.EnglishName;
        }

        return !string.IsNullOrWhiteSpace(item.Id) ? CleanInternalId(item.Id) : "";
    }

    private static void DrawItemIcon(CatalogItem item, float size)
    {
        DrawIconImage(item.IconId, size);
    }

    private static bool DrawEntitySpriteImage(string id, float size)
    {
        if (!TryGetEntitySpriteImage(id, out nint textureId, out Vector2 uv0, out Vector2 uv1))
        {
            return false;
        }

        ImGui.Image((IntPtr)textureId, new Vector2(size, size), uv0, uv1);
        return true;
    }

    private static bool DrawOptionalIconImage(string? iconId, float size)
    {
        if (!TryGetIconImage(iconId, out nint textureId, out Vector2 uv0, out Vector2 uv1))
        {
            return false;
        }

        ImGui.Image((IntPtr)textureId, new Vector2(size, size), uv0, uv1);
        return true;
    }

    private static void DrawIconImage(string? iconId, float size)
    {
        if (TryGetIconImage(iconId, out nint textureId, out Vector2 uv0, out Vector2 uv1))
        {
            ImGui.Image((IntPtr)textureId, new Vector2(size, size), uv0, uv1);
            return;
        }

        Vector2 pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size, size));
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + new Vector2(size, size), ImGui.GetColorU32(new Vector4(0.11f, 0.18f, 0.18f, 1f)), 2f);
        drawList.AddRect(pos, pos + new Vector2(size, size), ImGui.GetColorU32(new Vector4(0.26f, 0.42f, 0.38f, 1f)), 2f);
    }

    private static bool TryGetIconImage(string? iconId, out nint textureId, out Vector2 uv0, out Vector2 uv1)
    {
        textureId = 0;
        uv0 = Vector2.Zero;
        uv1 = Vector2.One;
        if (string.IsNullOrWhiteSpace(iconId) || Globals.ImGuiRenderer == null)
        {
            return false;
        }

        try
        {
            IconData? iconData = IconDataBase.GetIconOrNull(iconId);
            if (!iconData.HasValue)
            {
                return false;
            }

            IconData value = iconData.Value;
            GameIcon icon = value.GetIconOrDefault((IconFlag)1);
            Texture2D texture = icon.SpriteSheet.Texture;
            if (!iconTextureIds.TryGetValue(texture, out textureId))
            {
                textureId = Globals.ImGuiRenderer.BindTexture(texture);
                iconTextureIds[texture] = textureId;
            }

            XnaRectangle frame = icon.SpriteSheet.GetFrame(icon.Frame);
            uv0 = new Vector2((float)frame.X / texture.Width, (float)frame.Y / texture.Height);
            uv1 = new Vector2((float)(frame.X + frame.Width) / texture.Width, (float)(frame.Y + frame.Height) / texture.Height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetEntitySpriteImage(string id, out nint textureId, out Vector2 uv0, out Vector2 uv1)
    {
        textureId = 0;
        uv0 = Vector2.Zero;
        uv1 = Vector2.One;
        if (Globals.ImGuiRenderer == null || !TryGetEntityBaseGuid(id, out Guid baseGuid))
        {
            return false;
        }

        try
        {
            if (!DoodadDatabaseManager.TryGetEntityBaseData(baseGuid, out EntityWrapper? entity))
            {
                return false;
            }

            SpriteSheet spriteSheet = entity.SpriteSheet;
            Texture2D texture = spriteSheet.Texture;
            if (!iconTextureIds.TryGetValue(texture, out textureId))
            {
                textureId = Globals.ImGuiRenderer.BindTexture(texture);
                iconTextureIds[texture] = textureId;
            }

            XnaRectangle frame = spriteSheet.GetFrame(Math.Max(0, entity.Frame));
            uv0 = new Vector2((float)frame.X / texture.Width, (float)frame.Y / texture.Height);
            uv1 = new Vector2((float)(frame.X + frame.Width) / texture.Width, (float)(frame.Y + frame.Height) / texture.Height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string RunCommand(string command)
    {
        try
        {
            EnableCheatsForTerminalCommands();
            string? result = Globals.Game?.Terminal?.DoCommand(command);
            return result ?? T("游戏控制台尚未就绪。", "Game terminal is not ready yet.");
        }
        catch (Exception ex)
        {
            return T("执行失败：", "Command failed: ") + ex.Message;
        }
    }

    private static string StartMouseEntityPlacement(IdDropdownEntry entity)
    {
        if (Globals.Game?.Player == null || GameState.CurrentWorld == null)
        {
            return T("请先进入存档。", "Enter a save first.");
        }

        if (!TryResolveEntityBaseId(entity.Id, out _))
        {
            return T("无法识别实体 ID：", "Could not resolve entity ID: ") + entity.Id;
        }

        pendingMouseEntityPlacementId = entity.Id;
        pendingMouseEntityPlacementName = entity.DisplayName;
        pendingMouseEntityPlacementReady = false;
        RequestTrainerClose();
        return T(
            $"已进入鼠标放置：{entity.DisplayName}。松开鼠标后左键生成，右键或 ESC 取消。",
            $"Mouse placement armed: {entity.DisplayName}. Release the mouse, then left-click to spawn; right-click or Esc cancels.");
    }

    private static void RequestTrainerClose()
    {
        Interlocked.Exchange(ref trainerCloseRequested, 1);
    }

    private static void HandlePendingMouseEntityPlacement()
    {
        if (string.IsNullOrWhiteSpace(pendingMouseEntityPlacementId))
        {
            return;
        }

        if (Globals.Game?.Player == null || GameState.CurrentWorld == null)
        {
            CancelMouseEntityPlacement(T("鼠标放置已取消：请先进入存档。", "Mouse placement cancelled: enter a save first."));
            return;
        }

        if (!pendingMouseEntityPlacementReady)
        {
            if (InputManager.Up(MouseButtons.Left))
            {
                pendingMouseEntityPlacementReady = true;
            }
            return;
        }

        if (InputManager.Pressed(MouseButtons.Right) || InputManager.Pressed(Keys.Escape))
        {
            CancelMouseEntityPlacement(T("已取消鼠标放置实体。", "Mouse entity placement cancelled."));
            return;
        }

        if (!InputManager.Pressed(MouseButtons.Left))
        {
            return;
        }

        string entityId = pendingMouseEntityPlacementId;
        string entityName = pendingMouseEntityPlacementName;
        pendingMouseEntityPlacementId = "";
        pendingMouseEntityPlacementName = "";
        pendingMouseEntityPlacementReady = false;
        status = SpawnEntityAtMousePosition(entityId, entityName);
    }

    private static void CancelMouseEntityPlacement(string message)
    {
        pendingMouseEntityPlacementId = "";
        pendingMouseEntityPlacementName = "";
        pendingMouseEntityPlacementReady = false;
        status = message;
    }

    private static string SpawnEntityAtMousePosition(string entityId, string entityName)
    {
        Microsoft.Xna.Framework.Vector2 position = InputManager.MousePosition;
        return SpawnEntityAtPosition(entityId, entityName, position);
    }

    private static string SpawnEntityAtPosition(string entityId, string entityName, Microsoft.Xna.Framework.Vector2 position)
    {
        try
        {
            if (Globals.Game?.Player == null || GameState.CurrentWorld == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            if (!TryResolveEntityBaseId(entityId, out Guid baseId))
            {
                return T("无法识别实体 ID：", "Could not resolve entity ID: ") + entityId;
            }

            Microsoft.Xna.Framework.Vector3 spawnPosition = new(position.X, position.Y, 0f);
            if (!NetworkManager.ClientActive)
            {
                EntityWrapper? entity = DoodadDatabaseManager.SpawnDoodad(baseId, GameState.EntitySystem, initializeAi: false);
                if (entity == null)
                {
                    return T("生成实体失败：实体数量可能已达上限。", "Failed to spawn entity: entity limit may have been reached.");
                }

                entity.Direction = Globals.Game.Player.Direction;
                entity.Position = spawnPosition;
                entity.Name += "_romstar_mouse_spawned";
                entity.Controller?.EntityInitialize();
                entity.Controller?.WorldInitialize();
                entity.Controller?.WorldLoad();
                return T($"已在鼠标位置生成实体：{entityName}", $"Spawned entity at mouse position: {entityName}");
            }

            EntityService.SendRequestSpawnEntity(new RequestSpawnEntityMessage
            {
                Position = spawnPosition,
                Direction = Globals.Game.Player.Direction,
                Velocity = Microsoft.Xna.Framework.Vector3.Zero,
                EntityBaseId = baseId,
                WorldId = GameState.CurrentWorld.Id,
                Parameters = null
            });
            return T($"已请求在鼠标位置生成实体：{entityName}", $"Requested entity spawn at mouse position: {entityName}");
        }
        catch (Exception ex)
        {
            return T("鼠标位置生成实体失败：", "Failed to spawn entity at mouse position: ") + ex.Message;
        }
    }

    private static bool TryResolveEntityBaseId(string entityId, out Guid baseId)
    {
        return Guid.TryParse(entityId, out baseId) ||
            DoodadDatabaseManager.Names.TryGetValue(entityId, out baseId);
    }

    private static string SpawnWorldItem(string itemId, int amount)
    {
        try
        {
            if (Globals.Game?.Player == null || GameState.CurrentWorld == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            amount = Math.Clamp(amount, 1, MaxCommandAmount);
            Microsoft.Xna.Framework.Vector3 position = Globals.Game.Player.Position + new Microsoft.Xna.Framework.Vector3(0f, 0f, 24f);
            Microsoft.Xna.Framework.Vector3 velocity = new(0f, 8f, 24f);
            return WorldItemServerManager.SpawnNewItemAsWorldItem(itemId, amount, position, velocity, GameState.CurrentWorld.Id) == null
                ? T("生成地图物品失败。", "Failed to spawn world item.")
                : T($"已在脚下生成地图物品：{amount} 个。", $"Spawned world item at feet: {amount}.");
        }
        catch (Exception ex)
        {
            return T("生成地图物品失败：", "Failed to spawn world item: ") + ex.Message;
        }
    }

    private static void EnableCheatsForTerminalCommands()
    {
        try
        {
            GameState.Config.CheatsEnabled = true;
            ServerGameState.Config.CheatsEnabled = true;
            GameStateService.Send_UpdateGameConfig(GameState.Config);
        }
        catch
        {
        }
    }

    private static void ApplySafeInstantConstruction()
    {
        Cheats.InstantActions = false;
        if (!instantConstruction)
        {
            return;
        }

        try
        {
            bool builtAny = false;
            foreach (ConstructionSite site in GameState.ConstructionSites.Values.ToList())
            {
                if (site == null || site.CanBuild != ConstructionHelper.ConstructionValidEnum.Valid)
                {
                    continue;
                }

                if (TryBuildConstructionSiteInstantly(site.Id))
                {
                    builtAny = true;
                }
            }

            if (builtAny)
            {
                return;
            }

            foreach (ConstructionSite site in GameState.ConstructionSites.Values)
            {
                if (site == null ||
                    site.CanBuild != ConstructionHelper.ConstructionValidEnum.Valid ||
                    site.RequiredConstructionProgress <= 0f ||
                    site.CurrentConstructionProgress >= site.RequiredConstructionProgress)
                {
                    continue;
                }

                float progress = Math.Max(
                    site.RequiredConstructionProgress - site.CurrentConstructionProgress,
                    site.RequiredConstructionProgress);
                ConstructionSitesService.Send_ConstructActionInProgressDoProgress(new ConstructActionInProgressDoProgressMessage
                {
                    Progress = progress,
                    ConstructionSiteId = site.Id
                });
            }
        }
        catch
        {
            // Construction site lists can change while the game updates; retry on the next tick.
        }
    }

    private static bool TryBuildConstructionSiteInstantly(Guid constructionSiteId)
    {
        try
        {
            if (!ServerGameState.ConstructionSites.ContainsKey(constructionSiteId))
            {
                return false;
            }

            ConstructionSitesServerManager.TryBuildConstructionSite(
                constructionSiteId,
                ServerGlobals.CurrentPlayerCharacter?.Character);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AnyPlayerSustainEnabled()
    {
        return keepPlayerHealth ||
            keepPlayerEnergy ||
            keepPlayerMaxEnergy ||
            keepPlayerEnergyRegeneration ||
            keepPlayerAttackSpeed ||
            keepPlayerMoveSpeed ||
            playerPersistentStatOverrides.Count > 0;
    }

    private static string EnableKeepPlayerHealth()
    {
        EntityWrapper? player = GetPlayerForSustain();
        if (player == null)
        {
            keepPlayerHealth = false;
            return T("请先进入存档。", "Enter a save first.");
        }

        CaptureOriginalHealth(player);
        return T("已开启生命值保持。", "Health keep enabled.");
    }

    private static string DisableKeepPlayerHealth()
    {
        keepPlayerHealth = false;
        return RestorePlayerHealth();
    }

    private static string EnableKeepPlayerEnergy()
    {
        EntityWrapper? player = GetPlayerForSustain();
        if (player == null)
        {
            keepPlayerEnergy = false;
            return T("请先进入存档。", "Enter a save first.");
        }

        CaptureOriginalEnergy(player);
        return T("已开启当前能量保持。", "Current energy keep enabled.");
    }

    private static string DisableKeepPlayerEnergy()
    {
        keepPlayerEnergy = false;
        return RestorePlayerEnergy();
    }

    private static string EnableKeepPlayerMaxEnergy()
    {
        EntityWrapper? player = GetPlayerForSustain();
        if (player == null)
        {
            keepPlayerMaxEnergy = false;
            return T("请先进入存档。", "Enter a save first.");
        }

        CaptureOriginalEnergy(player);
        return T("已开启最大能量保持。", "Max energy keep enabled.");
    }

    private static string DisableKeepPlayerMaxEnergy()
    {
        keepPlayerMaxEnergy = false;
        return RestorePlayerMaxEnergy();
    }

    private static string EnableKeepPlayerEnergyRegeneration()
    {
        EntityWrapper? player = GetPlayerForSustain();
        if (player == null)
        {
            keepPlayerEnergyRegeneration = false;
            return T("请先进入存档。", "Enter a save first.");
        }

        CaptureOriginalBaseStat(player, "EnergyRegeneration", ref originalPlayerEnergyRegeneration);
        return T("已开启能量恢复保持。", "Energy regen keep enabled.");
    }

    private static string DisableKeepPlayerEnergyRegeneration()
    {
        keepPlayerEnergyRegeneration = false;
        return RestorePlayerBaseStat("EnergyRegeneration", ref originalPlayerEnergyRegeneration, T("能量恢复", "Energy Regen"));
    }

    private static string EnableKeepPlayerAttackSpeed()
    {
        EntityWrapper? player = GetPlayerForSustain();
        if (player == null)
        {
            keepPlayerAttackSpeed = false;
            return T("请先进入存档。", "Enter a save first.");
        }

        CaptureOriginalBaseStat(player, "AttackSpeed", ref originalPlayerAttackSpeed);
        return T("已开启攻击速度保持。", "Attack speed keep enabled.");
    }

    private static string DisableKeepPlayerAttackSpeed()
    {
        keepPlayerAttackSpeed = false;
        return RestorePlayerBaseStat("AttackSpeed", ref originalPlayerAttackSpeed, T("攻击速度", "Attack Speed"));
    }

    private static string EnableKeepPlayerMoveSpeed()
    {
        EntityWrapper? player = GetPlayerForSustain();
        if (player == null)
        {
            keepPlayerMoveSpeed = false;
            return T("请先进入存档。", "Enter a save first.");
        }

        originalPlayerAccelerationScale ??= player.AccelerationScale;
        return T("已开启移动速度保持。", "Move speed keep enabled.");
    }

    private static string DisableKeepPlayerMoveSpeed()
    {
        keepPlayerMoveSpeed = false;
        if (!originalPlayerAccelerationScale.HasValue)
        {
            return T("已关闭移动速度保持。", "Move speed keep disabled.");
        }

        float original = originalPlayerAccelerationScale.Value;
        originalPlayerAccelerationScale = null;
        EntityWrapper? player = Globals.Game?.Player;
        if (player != null)
        {
            player.AccelerationScale = original;
            SyncServerPlayerEntity(entity => entity.AccelerationScale = original);
        }

        return T("已关闭移动速度保持并恢复原值。", "Move speed keep disabled and restored.");
    }

    private static EntityWrapper? GetPlayerForSustain()
    {
        EntityWrapper? player = Globals.Game?.Player;
        Guid? entityId = GameState.LocalPlayer?.EntityId;
        if (sustainedPlayerEntityId != entityId)
        {
            ClearPlayerOriginals();
            sustainedPlayerEntityId = entityId;
        }

        return player;
    }

    private static void ClearPlayerOriginals()
    {
        originalPlayerHealth = null;
        originalPlayerMaxHealth = null;
        originalPlayerHealthBase = null;
        originalPlayerEnergy = null;
        originalPlayerEnergyBase = null;
        originalPlayerEnergyRegeneration = null;
        originalPlayerAttackSpeed = null;
        originalPlayerAccelerationScale = null;
        playerPersistentStatOriginals.Clear();
    }

    private static void CaptureOriginalHealth(EntityWrapper player)
    {
        originalPlayerHealth ??= player.Health;
        originalPlayerMaxHealth ??= player.MaxHealth;
        originalPlayerHealthBase ??= player.Stats.Get("Health");
    }

    private static void CaptureOriginalEnergy(EntityWrapper player)
    {
        originalPlayerEnergy ??= player.Energy;
        originalPlayerEnergyBase ??= player.Stats.Get("Energy");
    }

    private static void CaptureOriginalBaseStat(EntityWrapper player, string statId, ref float? originalValue)
    {
        originalValue ??= player.Stats.Get(statId);
    }

    private static void CaptureOriginalForBaseStat(EntityWrapper player, string statId)
    {
        switch (statId)
        {
            case "Energy":
                CaptureOriginalEnergy(player);
                break;
            case "EnergyRegeneration":
                CaptureOriginalBaseStat(player, statId, ref originalPlayerEnergyRegeneration);
                break;
            case "AttackSpeed":
                CaptureOriginalBaseStat(player, statId, ref originalPlayerAttackSpeed);
                break;
        }
    }

    private static string RestorePlayerHealth()
    {
        if (!originalPlayerHealth.HasValue && !originalPlayerMaxHealth.HasValue && !originalPlayerHealthBase.HasValue)
        {
            return T("已关闭生命值保持。", "Health keep disabled.");
        }

        float health = originalPlayerHealth ?? 1f;
        float maxHealth = originalPlayerMaxHealth ?? Math.Max(health, 1f);
        float baseHealth = originalPlayerHealthBase ?? maxHealth;
        originalPlayerHealth = null;
        originalPlayerMaxHealth = null;
        originalPlayerHealthBase = null;
        EntityWrapper? player = Globals.Game?.Player;
        if (player != null)
        {
            RestoreHealth(player, health, maxHealth, baseHealth);
            SyncServerPlayerEntity(entity => RestoreHealth(entity, health, maxHealth, baseHealth));
        }

        return T("已关闭生命值保持并恢复原值。", "Health keep disabled and restored.");
    }

    private static void RestoreHealth(EntityWrapper player, float health, float maxHealth, float baseHealth)
    {
        SetPlayerStatRaw(player, "Health", Math.Max(1f, baseHealth));
        player.MaxHealth = Math.Max(1f, maxHealth);
        player.Health = Math.Clamp(health, 1f, player.MaxHealth);
    }

    private static string RestorePlayerEnergy()
    {
        if (!originalPlayerEnergy.HasValue)
        {
            return T("已关闭当前能量保持。", "Current energy keep disabled.");
        }

        float energy = originalPlayerEnergy.Value;
        originalPlayerEnergy = null;
        EntityWrapper? player = Globals.Game?.Player;
        if (player != null)
        {
            RestoreEnergy(player, energy);
            SyncServerPlayerEntity(entity => RestoreEnergy(entity, energy));
        }

        return T("已关闭当前能量保持并恢复原值。", "Current energy keep disabled and restored.");
    }

    private static void RestoreEnergy(EntityWrapper player, float energy)
    {
        float maxEnergy = Math.Max(1f, player.Stats.Get("Energy"));
        player.Energy = Math.Clamp(energy, 0f, maxEnergy);
    }

    private static string RestorePlayerMaxEnergy()
    {
        if (!originalPlayerEnergyBase.HasValue)
        {
            return T("已关闭最大能量保持。", "Max energy keep disabled.");
        }

        float baseEnergy = Math.Max(1f, originalPlayerEnergyBase.Value);
        originalPlayerEnergyBase = null;
        EntityWrapper? player = Globals.Game?.Player;
        if (player != null)
        {
            RestoreMaxEnergy(player, baseEnergy);
            SyncServerPlayerEntity(entity => RestoreMaxEnergy(entity, baseEnergy));
        }

        return T("已关闭最大能量保持并恢复原值。", "Max energy keep disabled and restored.");
    }

    private static void RestoreMaxEnergy(EntityWrapper player, float baseEnergy)
    {
        SetPlayerStatRaw(player, "Energy", baseEnergy);
        player.Energy = Math.Min(player.Energy, Math.Max(1f, player.Stats.Get("Energy")));
    }

    private static string RestorePlayerBaseStat(string statId, ref float? originalValue, string label)
    {
        if (!originalValue.HasValue)
        {
            return T($"已关闭{label}保持。", $"{label} keep disabled.");
        }

        float value = originalValue.Value;
        originalValue = null;
        EntityWrapper? player = Globals.Game?.Player;
        if (player != null)
        {
            SetPlayerStatRaw(player, statId, value);
            SyncServerPlayerEntity(entity => SetPlayerStatRaw(entity, statId, value));
        }

        return T($"已关闭{label}保持并恢复原值。", $"{label} keep disabled and restored.");
    }

    private static void ApplySustainedPlayerModifiers()
    {
        EntityWrapper? player = GetPlayerForSustain();
        if (player == null)
        {
            return;
        }

        if (keepPlayerHealth)
        {
            CaptureOriginalHealth(player);
            ApplyPlayerHealth(player, playerHealth);
            SyncServerPlayerEntity(entity => ApplyPlayerHealth(entity, playerHealth));
        }

        if (keepPlayerEnergy)
        {
            CaptureOriginalEnergy(player);
            ApplyPlayerEnergy(player, playerEnergy);
            SyncServerPlayerEntity(entity => ApplyPlayerEnergy(entity, playerEnergy));
        }

        if (keepPlayerMaxEnergy)
        {
            CaptureOriginalEnergy(player);
            ApplyPlayerBaseStat(player, "Energy", playerMaxEnergy);
            SyncServerPlayerEntity(entity => ApplyPlayerBaseStat(entity, "Energy", playerMaxEnergy));
        }

        if (keepPlayerEnergyRegeneration)
        {
            CaptureOriginalBaseStat(player, "EnergyRegeneration", ref originalPlayerEnergyRegeneration);
            ApplyPlayerBaseStat(player, "EnergyRegeneration", playerEnergyRegeneration);
            SyncServerPlayerEntity(entity => ApplyPlayerBaseStat(entity, "EnergyRegeneration", playerEnergyRegeneration));
        }

        if (keepPlayerAttackSpeed)
        {
            CaptureOriginalBaseStat(player, "AttackSpeed", ref originalPlayerAttackSpeed);
            ApplyPlayerBaseStat(player, "AttackSpeed", playerAttackSpeed);
            SyncServerPlayerEntity(entity => ApplyPlayerBaseStat(entity, "AttackSpeed", playerAttackSpeed));
        }

        if (keepPlayerMoveSpeed)
        {
            originalPlayerAccelerationScale ??= player.AccelerationScale;
            float accelerationScale = Math.Clamp(speedMultiplier, 1, 20) * 2560f;
            player.AccelerationScale = accelerationScale;
            SyncServerPlayerEntity(entity => entity.AccelerationScale = accelerationScale);
        }
    }

    private static string SetPlayerHealth(int value)
    {
        try
        {
            value = Math.Clamp(value, 1, 999999);
            EntityWrapper? player = Globals.Game?.Player;
            if (player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            CaptureOriginalHealth(player);
            ApplyPlayerHealth(player, value);
            SyncServerPlayerEntity(entity => ApplyPlayerHealth(entity, value));
            return T($"已设置生命值并同步最大生命：{value}。", $"Set health and synced max health: {value}.");
        }
        catch (Exception ex)
        {
            return T("设置生命值失败：", "Failed to set health: ") + ex.Message;
        }
    }

    private static void ApplyPlayerHealth(EntityWrapper player, float value)
    {
        value = Math.Clamp(value, 1f, 999999f);
        SetPlayerStatRaw(player, "Health", value);
        player.MaxHealth = Math.Max(player.MaxHealth, value);
        player.Health = value;
    }

    private static void SetPlayerStatRaw(EntityWrapper player, string statId, float value)
    {
        player.Stats.SetBaseStat(statId, value);
        if (statId == "Health")
        {
            player.MaxHealth = player.Stats.Get("Health");
        }
        else if (statId == "Energy")
        {
            player.Energy = Math.Min(player.Energy, Math.Max(1f, player.Stats.Get("Energy")));
        }
    }

    private static float GetPlayerStatValue(EntityWrapper player, string statId)
    {
        try
        {
            return player.Stats.Stats.TryGetValue(statId, out var stat)
                ? stat.CalculatedValue
                : player.Stats.Get(statId);
        }
        catch
        {
            return 0f;
        }
    }

    private static string SetPlayerPersistentStat(PlayerStatEditorDef stat, int value, bool keep)
    {
        try
        {
            value = Math.Clamp(value, stat.Min, stat.Max);
            EntityWrapper? player = Globals.Game?.Player;
            if (player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            if (keep && !playerPersistentStatOriginals.ContainsKey(stat.Id))
            {
                playerPersistentStatOriginals[stat.Id] = GetPlayerStatValue(player, stat.Id);
            }

            SetPlayerStatRaw(player, stat.Id, value);
            SyncServerPlayerEntity(entity => SetPlayerStatRaw(entity, stat.Id, value));
            if (keep)
            {
                playerPersistentStatOverrides[stat.Id] = value;
            }

            return keep
                ? T($"已设置并保持玩家属性：{PlayerStatLabel(stat.Id)} = {value}。", $"Set and kept player stat: {PlayerStatLabel(stat.Id)} = {value}.")
                : T($"已设置玩家属性：{PlayerStatLabel(stat.Id)} = {value}。", $"Set player stat: {PlayerStatLabel(stat.Id)} = {value}.");
        }
        catch (Exception ex)
        {
            return T("设置玩家属性失败：", "Failed to set player stat: ") + ex.Message;
        }
    }

    private static string DisablePlayerPersistentStat(string statId, bool restoreOriginal)
    {
        playerPersistentStatOverrides.Remove(statId);
        if (!restoreOriginal || !playerPersistentStatOriginals.TryGetValue(statId, out float original))
        {
            playerPersistentStatOriginals.Remove(statId);
            return T($"已关闭属性保持：{PlayerStatLabel(statId)}。", $"Stat keep disabled: {PlayerStatLabel(statId)}.");
        }

        playerPersistentStatOriginals.Remove(statId);
        EntityWrapper? player = Globals.Game?.Player;
        if (player != null)
        {
            SetPlayerStatRaw(player, statId, original);
            SyncServerPlayerEntity(entity => SetPlayerStatRaw(entity, statId, original));
        }

        playerStatDraftValues[statId] = Math.Clamp((int)MathF.Round(original), 0, 999999);
        return T($"已关闭属性保持并恢复原值：{PlayerStatLabel(statId)}。", $"Stat keep disabled and restored: {PlayerStatLabel(statId)}.");
    }

    private static void ApplyPlayerPersistentStatOverrides()
    {
        if (playerPersistentStatOverrides.Count == 0)
        {
            return;
        }

        EntityWrapper? player = Globals.Game?.Player;
        if (player == null || player.Removed)
        {
            return;
        }

        ApplyPlayerPersistentStatOverridesTo(player);
        SyncServerPlayerEntity(ApplyPlayerPersistentStatOverridesTo);
    }

    private static void ApplyPlayerPersistentStatOverridesTo(EntityWrapper player)
    {
        foreach ((string statId, float value) in playerPersistentStatOverrides)
        {
            SetPlayerStatRaw(player, statId, value);
        }
    }

    private static string ClearPlayerPersistentStatOverrides()
    {
        if (playerPersistentStatOverrides.Count == 0)
        {
            return T("当前没有持续保持的玩家属性。", "No player stat keeps are active.");
        }

        string[] statIds = playerPersistentStatOverrides.Keys.ToArray();
        foreach (string statId in statIds)
        {
            DisablePlayerPersistentStat(statId, restoreOriginal: true);
        }

        return T("已清除玩家属性持续保持并尽量恢复原值。", "Cleared player stat keeps and restored original values where possible.");
    }

    private static string SetPlayerEnergy(int value)
    {
        try
        {
            value = Math.Clamp(value, 0, 999999);
            EntityWrapper? player = Globals.Game?.Player;
            if (player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            CaptureOriginalEnergy(player);
            ApplyPlayerEnergy(player, value);
            SyncServerPlayerEntity(entity => ApplyPlayerEnergy(entity, value));
            return T($"已设置当前能量：{value}。", $"Set current energy: {value}.");
        }
        catch (Exception ex)
        {
            return T("设置当前能量失败：", "Failed to set current energy: ") + ex.Message;
        }
    }

    private static void ApplyPlayerEnergy(EntityWrapper player, float value)
    {
        player.Energy = Math.Clamp(value, 0f, 999999f);
    }

    private static string SetPlayerBaseStat(string statId, int value, string label)
    {
        try
        {
            value = Math.Clamp(value, statId == "Energy" || statId == "AttackSpeed" ? 1 : 0, 999999);
            EntityWrapper? player = Globals.Game?.Player;
            if (player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            CaptureOriginalForBaseStat(player, statId);
            ApplyPlayerBaseStat(player, statId, value);
            SyncServerPlayerEntity(entity => ApplyPlayerBaseStat(entity, statId, value));
            return T($"已设置{label}：{value}。", $"Set {label}: {value}.");
        }
        catch (Exception ex)
        {
            return T($"设置{label}失败：", $"Failed to set {label}: ") + ex.Message;
        }
    }

    private static void ApplyPlayerBaseStat(EntityWrapper player, string statId, float value)
    {
        SetPlayerStatRaw(player, statId, value);
        if (statId == "Energy")
        {
            float maxEnergy = Math.Max(1f, player.Stats.Get("Energy"));
            player.Energy = Math.Min(player.Energy, maxEnergy);
        }
    }

    private static void SyncServerPlayerEntity(Action<EntityWrapper> update)
    {
        var localPlayer = GameState.LocalPlayer;
        Guid? entityId = localPlayer?.EntityId;
        if (entityId.HasValue &&
            ServerGameState.TryGetServerEntity(entityId.Value, out ServerEntityModel? entity, "RomStarPlayerHealth") &&
            entity?.EntityWrapper != null)
        {
            update(entity.EntityWrapper);
        }
    }

    private static string SetPlayerMoveSpeed(int multiplier)
    {
        try
        {
            multiplier = Math.Clamp(multiplier, 1, 20);
            float accelerationScale = multiplier * 2560f;
            EntityWrapper? player = Globals.Game?.Player;
            if (player == null)
            {
                return T("请先进入存档。", "Enter a save first.");
            }

            originalPlayerAccelerationScale ??= player.AccelerationScale;
            player.AccelerationScale = accelerationScale;
            SyncServerPlayerEntity(entity => entity.AccelerationScale = accelerationScale);
            return T($"已设置移动速度倍率：{multiplier}。", $"Set move speed multiplier: {multiplier}.");
        }
        catch (Exception ex)
        {
            return T("设置移动速度失败：", "Failed to set move speed: ") + ex.Message;
        }
    }

    private static void SetNoConstructionMaterials(bool enabled)
    {
        try
        {
            noConstructionMaterials = enabled;
            GameState.Config.DisableConstructionMaterialRequirements = enabled;
            ServerGameState.Config.DisableConstructionMaterialRequirements = enabled;
            GameStateService.Send_UpdateGameConfig(GameState.Config);
            status = enabled ? T("已开启忽略建造材料。", "Ignore construction materials enabled.") : T("已关闭忽略建造材料。", "Ignore construction materials disabled.");
        }
        catch (Exception ex)
        {
            status = T("设置忽略建造材料失败：", "Failed to set ignore construction materials: ") + ex.Message;
        }
    }

    private static string SetSpawnRateMultiplier(int value)
    {
        value = Math.Clamp(value, 0, 100);
        spawnRateMultiplier = value;
        return ApplyGameConfigValue(value, T("刷怪速率倍率", "Spawn rate multiplier"), static v =>
        {
            GameState.Config.EntitySpawnRateMultiplier = v;
            ServerGameState.Config.EntitySpawnRateMultiplier = v;
        });
    }

    private static string SetCropGrowthMultiplier(int value)
    {
        value = Math.Clamp(value, 0, 100);
        cropGrowthMultiplier = value;
        return ApplyGameConfigValue(value, T("作物生长倍率", "Crop growth multiplier"), static v =>
        {
            GameState.Config.CropGrowthSpeedMultiplier = v;
            ServerGameState.Config.CropGrowthSpeedMultiplier = v;
        });
    }

    private static string SetJobSpeedMultiplier(int value)
    {
        value = Math.Clamp(value, 0, 100);
        jobSpeedMultiplier = value;
        return ApplyGameConfigValue(value, T("工作速度倍率", "Job speed multiplier"), static v =>
        {
            GameState.Config.JobTickSpeedMultiplier = v;
            ServerGameState.Config.JobTickSpeedMultiplier = v;
        });
    }

    private static string SetJobProgressMultiplier(int value)
    {
        value = Math.Clamp(value, 0, 100);
        jobProgressMultiplier = value;
        return ApplyGameConfigValue(value, T("工作进度倍率", "Job progress multiplier"), static v =>
        {
            GameState.Config.JobProgressMultiplier = v;
            ServerGameState.Config.JobProgressMultiplier = v;
        });
    }

    private static string ApplyGameConfigValue(float value, string label, Action<float> apply)
    {
        try
        {
            apply(value);
            GameStateService.Send_UpdateGameConfig(GameState.Config);
            return T($"已设置{label}：{value:0.##}。", $"Set {label}: {value:0.##}.");
        }
        catch (Exception ex)
        {
            return T($"设置{label}失败：", $"Failed to set {label}: ") + ex.Message;
        }
    }

    private static void SetCitizenPersistentBasicOverride(CitizenModel citizen, string fieldId, float value)
    {
        EnsureCitizenPersistentOverridesLoaded();
        citizenPersistentBasicOverrides[BuildCitizenPersistentOverrideKey(citizen.Id, fieldId)] = value;
        SaveCitizenPersistentOverrides();
    }

    private static void SetCitizenPersistentJobOverride(CitizenModel citizen, string jobId, string fieldId, float value)
    {
        EnsureCitizenPersistentOverridesLoaded();
        citizenPersistentJobOverrides[BuildCitizenPersistentOverrideKey(citizen.Id, $"{jobId}|{fieldId}")] = value;
        SaveCitizenPersistentOverrides();
    }

    private static void SetCitizenPersistentStatOverride(CitizenModel citizen, string statId, float value)
    {
        EnsureCitizenPersistentOverridesLoaded();
        citizenPersistentStatOverrides[BuildCitizenPersistentOverrideKey(citizen.Id, statId)] = value;
        SaveCitizenPersistentOverrides();
    }

    private static string ClearCitizenPersistentOverrides(Guid citizenId)
    {
        EnsureCitizenPersistentOverridesLoaded();
        string prefix = citizenId.ToString("N") + "|";
        int removed = RemoveCitizenPersistentOverridesByPrefix(citizenPersistentBasicOverrides, prefix) +
            RemoveCitizenPersistentOverridesByPrefix(citizenPersistentJobOverrides, prefix) +
            RemoveCitizenPersistentOverridesByPrefix(citizenPersistentStatOverrides, prefix);
        SaveCitizenPersistentOverrides();
        return removed == 0
            ? T("当前 NPC 没有持续保持项。", "Current NPC has no kept values.")
            : T($"已清除当前 NPC 持续保持：{removed} 项。", $"Cleared current NPC kept values: {removed}.");
    }

    private static string ClearCitizenPersistentOverrides()
    {
        EnsureCitizenPersistentOverridesLoaded();
        int count = citizenPersistentBasicOverrides.Count + citizenPersistentJobOverrides.Count + citizenPersistentStatOverrides.Count;
        citizenPersistentBasicOverrides.Clear();
        citizenPersistentJobOverrides.Clear();
        citizenPersistentStatOverrides.Clear();
        SaveCitizenPersistentOverrides();
        return count == 0
            ? T("当前没有 NPC 持续保持项。", "No NPC kept values are active.")
            : T($"已清除全部 NPC 持续保持：{count} 项。", $"Cleared all NPC kept values: {count}.");
    }

    private static int RemoveCitizenPersistentOverridesByPrefix(Dictionary<string, float> overrides, string prefix)
    {
        int removed = 0;
        foreach (string key in overrides.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            overrides.Remove(key);
            removed++;
        }
        return removed;
    }

    private static void ApplyCitizenPersistentOverrides()
    {
        EnsureCitizenPersistentOverridesLoaded();
        if (citizenPersistentBasicOverrides.Count == 0 &&
            citizenPersistentJobOverrides.Count == 0 &&
            citizenPersistentStatOverrides.Count == 0)
        {
            return;
        }

        try
        {
            if (ServerGameState.Citizens == null)
            {
                return;
            }

            foreach (CitizenModel citizen in ServerGameState.Citizens.Values)
            {
                if (citizen != null)
                {
                    ApplyCitizenPersistentOverridesTo(citizen);
                }
            }
        }
        catch
        {
        }
    }

    private static void ApplyCitizenPersistentOverridesTo(CitizenModel citizen)
    {
        string prefix = citizen.Id.ToString("N") + "|";
        foreach (KeyValuePair<string, float> entry in citizenPersistentBasicOverrides.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            string fieldId = entry.Key[prefix.Length..];
            ApplyCitizenPersistentBasicOverride(citizen, fieldId, entry.Value);
        }

        foreach (KeyValuePair<string, float> entry in citizenPersistentJobOverrides.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            string[] parts = entry.Key[prefix.Length..].Split('|', 2);
            if (parts.Length == 2)
            {
                ApplyCitizenPersistentJobOverride(citizen, parts[0], parts[1], entry.Value);
            }
        }

        foreach (KeyValuePair<string, float> entry in citizenPersistentStatOverrides.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            string statId = entry.Key[prefix.Length..];
            if (citizen.CitizenStats.Stats != null && citizen.CitizenStats.Stats.TryGetValue(statId, out Stat? stat))
            {
                stat.SetBaseValue(entry.Value);
            }
        }
    }

    private static void ApplyCitizenPersistentBasicOverride(CitizenModel citizen, string fieldId, float value)
    {
        switch (fieldId)
        {
            case "Loyalty":
                citizen.Loyalty = Math.Clamp(value, 0f, 999999f);
                break;
            case "LoyaltyLevel":
                citizen.LoyaltyLevel = Math.Clamp((int)MathF.Round(value), 0, 4);
                if (citizen.LoyaltyLevel >= 4 && !citizenPersistentBasicOverrides.ContainsKey(BuildCitizenPersistentOverrideKey(citizen.Id, "Loyalty")))
                {
                    citizen.Loyalty = 0f;
                }
                break;
            case "CurrentHunger":
                citizen.CurrentHunger = Math.Clamp(value, 0f, 1f);
                break;
        }
    }

    private static void ApplyCitizenPersistentJobOverride(CitizenModel citizen, string jobId, string fieldId, float value)
    {
        if (citizen.JobExperience == null || !citizen.JobExperience.TryGetValue(jobId, out JobProgress? progress))
        {
            return;
        }

        switch (fieldId)
        {
            case "Level":
                progress.Level = Math.Clamp((int)MathF.Round(value), 0, 999);
                break;
            case "Experience":
                progress.Experience = Math.Clamp(value, 0f, 9999999f);
                break;
        }
    }

    private static string BuildCitizenPersistentOverrideKey(Guid citizenId, string id)
    {
        CitizenPersistentOverrideKey key = new(citizenId, id);
        return $"{key.CitizenId:N}|{key.Id}";
    }

    private static void EnsureCitizenPersistentOverridesLoaded()
    {
        if (citizenPersistentOverridesLoaded)
        {
            return;
        }

        citizenPersistentOverridesLoaded = true;
        string path = CitizenPersistentOverridesPath();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            CitizenPersistentOverrideState? state = JsonSerializer.Deserialize<CitizenPersistentOverrideState>(File.ReadAllText(path));
            LoadCitizenPersistentOverrideValues(citizenPersistentBasicOverrides, state?.Basic);
            LoadCitizenPersistentOverrideValues(citizenPersistentJobOverrides, state?.Job);
            LoadCitizenPersistentOverrideValues(citizenPersistentStatOverrides, state?.Stat);
        }
        catch
        {
            citizenPersistentBasicOverrides.Clear();
            citizenPersistentJobOverrides.Clear();
            citizenPersistentStatOverrides.Clear();
        }
    }

    private static void LoadCitizenPersistentOverrideValues(Dictionary<string, float> target, List<CitizenPersistentOverrideValue>? values)
    {
        target.Clear();
        if (values == null)
        {
            return;
        }

        foreach (CitizenPersistentOverrideValue value in values)
        {
            if (!string.IsNullOrWhiteSpace(value.Key))
            {
                target[value.Key] = value.Value;
            }
        }
    }

    private static void SaveCitizenPersistentOverrides()
    {
        try
        {
            CitizenPersistentOverrideState state = new(
                citizenPersistentBasicOverrides.Select(entry => new CitizenPersistentOverrideValue(entry.Key, entry.Value)).ToList(),
                citizenPersistentJobOverrides.Select(entry => new CitizenPersistentOverrideValue(entry.Key, entry.Value)).ToList(),
                citizenPersistentStatOverrides.Select(entry => new CitizenPersistentOverrideValue(entry.Key, entry.Value)).ToList());
            File.WriteAllText(CitizenPersistentOverridesPath(), JsonSerializer.Serialize(state));
        }
        catch
        {
        }
    }

    private static string CitizenPersistentOverridesPath()
    {
        string? directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = AppContext.BaseDirectory;
        }

        return Path.Combine(directory, "RomStar.CitizenOverrides.json");
    }

    private static void ApplyCitizenUnitToggles()
    {
        if (!citizenInvincible && !citizenNoHunger && !citizenHappy && !citizenLoyal)
        {
            return;
        }

        try
        {
            if (ServerGameState.Citizens == null)
            {
                return;
            }

            foreach (CitizenModel citizen in ServerGameState.Citizens.Values)
            {
                if (citizen == null)
                {
                    continue;
                }

                SanitizeCitizenState(citizen);
                if (citizenNoHunger)
                {
                    citizen.CurrentHunger = 0f;
                }
                if (citizenHappy)
                {
                    citizen.LoyaltyLevel = 4;
                    citizen.Loyalty = 0f;
                }
                if (citizenLoyal)
                {
                    citizen.LoyaltyLevel = Math.Min(Math.Max(citizen.LoyaltyLevel, 3), 4);
                    citizen.Loyalty = Math.Min(Math.Max(citizen.Loyalty, 999f), 9999f);
                }
                if (citizenInvincible && citizen.CitizenStats.Stats != null && citizen.CitizenStats.Stats.TryGetValue("Health", out var health))
                {
                    health.SetBaseValue(Math.Max(health.BaseValue, 99999f));
                }
            }
        }
        catch
        {
        }
    }

    private static void SanitizeCitizenState(CitizenModel citizen)
    {
        citizen.CurrentHunger = Math.Clamp(citizen.CurrentHunger, 0f, 0.95f);
        citizen.LoyaltyLevel = Math.Clamp(citizen.LoyaltyLevel, 0, 4);
        if (citizen.LoyaltyLevel >= 4)
        {
            citizen.Loyalty = 0f;
            return;
        }

        citizen.Loyalty = Math.Clamp(citizen.Loyalty, 0f, citizen.LoyaltyRequiredToLevelUp(citizen.LoyaltyLevel));
    }
}

