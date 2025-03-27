using Elements.Core;
using FrooxEngine.UIX;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using System.Collections.Generic;
using System.Reflection;
using System;
using System.Linq;

namespace InventorySearch
{
    public class InventorySearch : ResoniteMod
    {
        // Basic mod info, required but nobody ever reads this anyway or if you do then hello from me UwU
        public override string Name => "AdvancedInventorySearch";
        public override string Author => "xlinka";
        public override string Version => "1.0.0"; // should remember to bump this someday :shrug; 
        public override string Link => "https://github.com/xlinka/AdvancedInventorySearch";

        // Enums for search/sort - had to make these because Resonite's inventory system is from the stone age
        public enum SearchScope
        {
            All,
            FoldersOnly,
            ItemsOnly // why can't this just be built-in already??
        }

        public enum SortMethod
        {
            Default, // aka "whatever random order Resonite feels like today"
            AToZ,
            ZToA,
            RecentlyAdded, // the only one people actually use
            OldestFirst // nobody will use this but adding it anyway
        }

        // Config keys - probably overkill but whatever
        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> Enabled =
            new("enabled", "Enable Inventory Search", () => true);

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> CaseSensitive =
            new("caseSensitive", "Case-Sensitive Search", () => false); // who even uses case sensitive search??

        // More config nobody will change
        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<SearchScope> DefaultSearchScope =
            new("defaultSearchScope", "Default Search Scope", () => SearchScope.All);

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<SortMethod> DefaultSortMethod =
            new("defaultSortMethod", "Default Sort Method", () => SortMethod.RecentlyAdded);

        private static Dictionary<InventoryBrowser, SearchBarData> _searchBars = new Dictionary<InventoryBrowser, SearchBarData>();

        private static ModConfiguration Config;

        private static FieldInfo _itemField;
        private static FieldInfo _directoryField;

        public override void OnEngineInit()
        {
            Config = GetConfiguration();
            Harmony harmony = new Harmony("com.xlinka.Advancedinventorysearch");
            harmony.PatchAll(); 

            // Cache reflection fields to avoid repeated lookups because performance matters...sometimes
            _itemField = typeof(InventoryItemUI).GetField("Item", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _directoryField = typeof(InventoryItemUI).GetField("Directory", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            // Dear future me: if these fields change names in an update, everything breaks. Sorry about that.
        }

        private static SearchScope CycleThroughScopes(SearchScope currentScope)
        {
            return (SearchScope)(((int)currentScope + 1) % Enum.GetValues(typeof(SearchScope)).Length);
        }

        private static SortMethod CycleThroughSortMethods(SortMethod currentMethod)
        {
            return (SortMethod)(((int)currentMethod + 1) % Enum.GetValues(typeof(SortMethod)).Length);
        }

        // I hate hardcoded UI text but here we are
        private static string GetScopeButtonText(SearchScope scope)
        {
            switch (scope)
            {
                case SearchScope.All: return "All";
                case SearchScope.FoldersOnly: return "Folders";
                case SearchScope.ItemsOnly: return "Items";
                default: return "All"; // this default should never happen but watch it happen anyway
            }
        }

        // This patch injects our search bar when the browser loads items
        [HarmonyPatch(typeof(InventoryBrowser), "OnItemSelected")]
        public static class InventoryBrowserItemSelectedPatch
        {
            public static void Postfix(InventoryBrowser __instance, SyncRef<Slot> ____buttonsRoot)
            {
                if (!Config.GetValue(Enabled) || ____buttonsRoot.Target == null)
                    return;

        
                var existingSearchBar = __instance.Slot.FindChild(s => s.Name == "InventorySearchBar");
                if (existingSearchBar != null)
                    return;

                try
                {
                    // Get or create search bar data
                    if (!_searchBars.TryGetValue(__instance, out SearchBarData searchBarData))
                    {
                        searchBarData = new SearchBarData
                        {
                            CurrentSearchScope = Config.GetValue(DefaultSearchScope),
                            CurrentSortMethod = Config.GetValue(DefaultSortMethod)
                        };
                        _searchBars[__instance] = searchBarData;
                    }

                    // Add the search bar - pray that the parent hierarchy doesn't change in the next update
                    Slot buttonRoot = ____buttonsRoot.Target.Parent;

                    // Create search bar slot
                    Slot searchBarSlot = buttonRoot.AddSlot("InventorySearchBar");

                    // Positioning is a nightmare, these values are pure trial and error
                    var rt = searchBarSlot.AttachComponent<RectTransform>();
                    rt.AnchorMin.Value = new float2(0, 0);
                    rt.AnchorMax.Value = new float2(1, 0);
                    rt.OffsetMin.Value = new float2(0, 0); // Position at top of parent
                    rt.OffsetMax.Value = new float2(0, 30); 

                    HorizontalLayout layout = searchBarSlot.AttachComponent<HorizontalLayout>();
                    layout.Spacing.Value = 5f;
                    layout.PaddingLeft.Value = 10f;
                    layout.PaddingRight.Value = 10f;
                    layout.PaddingTop.Value = 0f;
                    layout.PaddingBottom.Value = 0f;

                    UIBuilder builder = new UIBuilder(searchBarSlot);

                    // Create the UI elements - hello callback hell my old friend
                    CreateSearchControls(builder, searchBarData, __instance);

                    // Store reference to the search bar slot
                    searchBarData.SearchBarSlot = searchBarSlot;
                }
                catch (Exception ex)
                {
                    // If this errors, at least tell me why
                    UniLog.Error($"InventorySearch error: {ex.Message}\n{ex.StackTrace}");
                }
            }

            // This should be broken down into smaller methods but I don't have the energy or care for this fact.
            private static void CreateSearchControls(UIBuilder ui, SearchBarData searchBarData, InventoryBrowser browser)
            {
                // Apply Resonite UI style - which may change entirely in the next update
                RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);

                // Create the scope button (All/Folders/Items)
                CreateScopeButton(ui, searchBarData, browser);

                CreateSortButton(ui, searchBarData, browser);

                // Create the search field - the whole point of this mod
                CreateSearchField(ui, searchBarData, browser);

                CreateClearButton(ui, searchBarData, browser);
            }

            private static void CreateScopeButton(UIBuilder ui, SearchBarData searchBarData, InventoryBrowser browser)
            {
                ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);
                ui.Style.MinHeight = 30;
                ui.PushStyle();

                var scopeButton = ui.Button(GetScopeButtonText(searchBarData.CurrentSearchScope));
                var buttonImage = scopeButton.Slot.GetComponentInChildren<Image>();
                if (buttonImage != null)
                {
                    buttonImage.Tint.Value = new colorX(0.4f, 0.7f, 0.9f, 0.8f); // blue-ish, looks ok I guess
                }

                var layoutElement = scopeButton.Slot.GetComponent<LayoutElement>();
                if (layoutElement == null)
                    layoutElement = scopeButton.Slot.AttachComponent<LayoutElement>();

                layoutElement.PreferredWidth.Value = 40f; 
                layoutElement.PreferredHeight.Value = 30f;

                scopeButton.Label.Size.Value = 14f;
                scopeButton.Label.Color.Value = RadiantUI_Constants.Neutrals.LIGHT;

                // Button logic - this callback will probably leak memory but whatever ?? i dont actually know but i will find out 
                scopeButton.LocalPressed += (btn, data) => {
                    searchBarData.CurrentSearchScope = CycleThroughScopes(searchBarData.CurrentSearchScope);
                    scopeButton.Label.Content.Value = GetScopeButtonText(searchBarData.CurrentSearchScope);
                    if (!string.IsNullOrEmpty(searchBarData.CurrentSearchText))
                        PerformSearch(browser, searchBarData, searchBarData.CurrentSearchText);
                };

                searchBarData.ScopeButton = scopeButton;
            }

            // Another copy-paste button
            private static void CreateSortButton(UIBuilder ui, SearchBarData searchBarData, InventoryBrowser browser)
            {
                ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);
                ui.Style.MinHeight = 30;
                ui.PushStyle();

                var sortButton = ui.Button(searchBarData.CurrentSortMethod.ToString());
                var buttonImage = sortButton.Slot.GetComponentInChildren<Image>();
                if (buttonImage != null)
                {
                    buttonImage.Tint.Value = new colorX(0.6f, 0.4f, 0.9f, 0.8f); // purple-ish to be different
                }

                var layoutElement = sortButton.Slot.GetComponent<LayoutElement>();
                if (layoutElement == null)
                    layoutElement = sortButton.Slot.AttachComponent<LayoutElement>();

                layoutElement.PreferredWidth.Value = 60f; // Can't be too small or text gets cut off
                layoutElement.PreferredHeight.Value = 30f;

                sortButton.Label.Size.Value = 14f;
                sortButton.Label.Color.Value = RadiantUI_Constants.Neutrals.LIGHT;

                sortButton.LocalPressed += (btn, data) => {
                    searchBarData.CurrentSortMethod = CycleThroughSortMethods(searchBarData.CurrentSortMethod);
                    sortButton.Label.Content.Value = searchBarData.CurrentSortMethod.ToString();
                    if (!string.IsNullOrEmpty(searchBarData.CurrentSearchText))
                        PerformSearch(browser, searchBarData, searchBarData.CurrentSearchText);
                };

                searchBarData.SortButton = sortButton;
            }

            // The actual search field 
            private static void CreateSearchField(UIBuilder ui, SearchBarData searchBarData, InventoryBrowser browser)
            {
                ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);
                ui.Style.MinHeight = 30;
                ui.Style.FlexibleHeight = 1f;
                ui.PushStyle();

                // Create the search field with placeholder
                var searchField = ui.TextField(searchBarData.CurrentSearchText, true, "", true, "<alpha=#77>Search..."); // placeholder hack
                searchField.Text.HorizontalAutoSize.Value = true;
                searchField.Text.Size.Value = 16f;
                searchField.Text.Color.Value = RadiantUI_Constants.Neutrals.LIGHT;
                searchField.Text.ParseRichText.Value = false; // don't need this mess

                searchField.Editor.Target.FinishHandling.Value = TextEditor.FinishAction.NullOnWhitespace;

                var layoutElement = searchField.Slot.GetComponent<LayoutElement>();
                if (layoutElement == null)
                    layoutElement = searchField.Slot.AttachComponent<LayoutElement>();

                layoutElement.PreferredWidth.Value = 150f;
                layoutElement.PreferredHeight.Value = 30f;
                layoutElement.FlexibleWidth.Value = 1f; // Make search field fill available space

                // Style the background - why isn't there a simple "set background color" method?
                var bgImage = searchField.Slot.GetComponentInChildren<Image>();
                if (bgImage != null)
                {
                    bgImage.Tint.Value = new colorX(0.15f, 0.15f, 0.15f, 0.9f); // dark gray
                }

                // Love event-driven programming...not
                searchField.Editor.Target.LocalEditingFinished += (Change) => {
                    var searchText = searchField.Editor.Target.Text.Target.Text;
                    searchBarData.CurrentSearchText = searchText ?? "";

                    // Only search if there's text to search for
                    if (!string.IsNullOrWhiteSpace(searchBarData.CurrentSearchText))
                    {
                        PerformSearch(browser, searchBarData, searchBarData.CurrentSearchText);
                    }
                    else
                    {
                        searchBarData.ClearSearch();
                        PerformSearch(browser, searchBarData, "");
                    }
                };

                searchBarData.InputText = searchField.Text;
            }

            private static void CreateClearButton(UIBuilder ui, SearchBarData searchBarData, InventoryBrowser browser)
            {
                ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);
                ui.Style.MinHeight = 30;
                ui.PushStyle();

                var clearButton = ui.Button("×"); // unicode X because why not
                var buttonImage = clearButton.Slot.GetComponentInChildren<Image>();
                if (buttonImage != null)
                {
                    buttonImage.Tint.Value = new colorX(0.8f, 0.3f, 0.3f, 0.8f); // reddish
                }

                var layoutElement = clearButton.Slot.GetComponent<LayoutElement>();
                if (layoutElement == null)
                    layoutElement = clearButton.Slot.AttachComponent<LayoutElement>();

                layoutElement.PreferredWidth.Value = 30f;
                layoutElement.PreferredHeight.Value = 30f;

                clearButton.Label.Size.Value = 18f; // bigger so the X doesn't look sad
                clearButton.Label.Color.Value = RadiantUI_Constants.Neutrals.LIGHT;

                clearButton.LocalPressed += (btn, data) => {
                    if (searchBarData.InputText != null)
                    {
                        searchBarData.InputText.Content.Value = "";
                    }
                    searchBarData.ClearSearch();
                    PerformSearch(browser, searchBarData, "");
                };

                searchBarData.ClearButton = clearButton;
            }
        }

        // All this state management for a simple search bar. UIX is a special kind of hell
        private class SearchBarData
        {
            public Slot SearchBarSlot;
            public Text PlaceholderText;
            public Text InputText;
            public Button ClearButton;
            public Button ScopeButton;
            public Button SortButton;
            public string CurrentSearchText = "";
            public SearchScope CurrentSearchScope = SearchScope.All;
            public SortMethod CurrentSortMethod = SortMethod.Default;
            public bool IsActive = false;
            // Dictionary to fix the mess when things get hidden
            public Dictionary<InventoryItemUI, bool> ItemVisibilityState = new Dictionary<InventoryItemUI, bool>();

            public void ClearSearch()
            {
                CurrentSearchText = "";
                IsActive = false;

                // Reset visibility - pray nothing was destroyed in the meantime
                foreach (var kvp in ItemVisibilityState)
                {
                    if (kvp.Key != null && !kvp.Key.Slot.IsDestroyed)
                    {
                        kvp.Key.Slot.ActiveSelf = true;
                    }
                }

                if (PlaceholderText != null)
                {
                    PlaceholderText.Enabled = true;
                }

                ItemVisibilityState.Clear();
            }
        }

        private static void PerformSearch(InventoryBrowser browser, SearchBarData searchBarData, string searchText)
        {
            if (browser == null || searchBarData == null)
                return;

            // Only activate if we have something to search for
            searchBarData.IsActive = !string.IsNullOrEmpty(searchText);

            // If nothing to search for, just reset everything
            if (!searchBarData.IsActive)
            {
                // Reset all items to visible
                List<InventoryItemUI> allItems = new List<InventoryItemUI>();
                browser.Slot.GetComponentsInChildren(allItems);
                foreach (var item in allItems)
                {
                    if (item != null && !item.Slot.IsDestroyed)
                    {
                        item.Slot.ActiveSelf = true;
                    }
                }
                return;
            }

            // Find all inventory items - this might get expensive with large inventories but ¯\_(ツ)_/¯
            List<InventoryItemUI> items = new List<InventoryItemUI>();
            browser.Slot.GetComponentsInChildren(items);

            // Reset tracking dictionary
            searchBarData.ItemVisibilityState.Clear();

            // Apply search to each item - n² complexity, don't tell anyone
            foreach (var item in items)
            {
                if (item != null && !item.Slot.IsDestroyed)
                {
                    bool wasVisible = item.Slot.ActiveSelf;
                    searchBarData.ItemVisibilityState[item] = wasVisible;

                    if (searchBarData.IsActive)
                    {
                        ApplySearchToItem(item, searchText, searchBarData.CurrentSearchScope);
                    }
                    else
                    {
                        // When search is cleared, make everything visible again
                        item.Slot.ActiveSelf = true;
                    }
                }
            }

            // Apply sorting to visible items
            SortVisibleItems(browser, searchBarData.CurrentSortMethod);
        }

        // The actual search logic - this could be way more efficient but it works
        private static void ApplySearchToItem(InventoryItemUI item, string searchText, SearchScope scope)
        {
            if (item == null || string.IsNullOrEmpty(searchText))
                return;

            // Use reflection because Resonite doesn't expose these properties properly
            bool isFolder = false;
            try
            {
                if (_directoryField != null)
                {
                    var directory = _directoryField.GetValue(item);
                    isFolder = directory != null;
                }
            }
            catch
            {
                // If reflection fails, default to not a folder
                isFolder = false; // why do I even bother with error handling
            }

            bool matchesScope = scope == SearchScope.All ||
                                (scope == SearchScope.FoldersOnly && isFolder) ||
                                (scope == SearchScope.ItemsOnly && !isFolder);

            bool matchesSearch = false;

            // Case sensitivity - nobody will use this option but it's here
            StringComparison comparison = Config.GetValue(CaseSensitive)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            // Check if name contains search text
            string itemName = item.ItemName;
            if (itemName != null)
            {
                matchesSearch = itemName.IndexOf(searchText, comparison) >= 0;
            }

            // Get the record to check tags
            object recordObj = null;

            try
            {
                if (_itemField != null)
                {
                    recordObj = _itemField.GetValue(item);
                }
            }
            catch
            {
                // Silently swallow errors, future me problem
            }

            // Check tags if we have a record
            if (!matchesSearch && recordObj != null && recordObj is FrooxEngine.Store.Record record)
            {
                if (record.Tags != null)
                {
                    foreach (var tag in record.Tags)
                    {
                        if (tag != null && tag.IndexOf(searchText, comparison) >= 0)
                        {
                            matchesSearch = true;
                            break;
                        }
                    }
                }
            }

            item.Slot.ActiveSelf = matchesSearch && matchesScope;
        }

        private static void SortVisibleItems(InventoryBrowser browser, SortMethod sortMethod)
        {
            // Find the GridLayout in the browser
            var gridLayout = browser.Slot.GetComponentInChildren<GridLayout>();
            if (gridLayout == null) return; // should never happen but watch it happen

            // Get all visible items
            List<InventoryItemUI> visibleItems = new List<InventoryItemUI>();
            browser.Slot.GetComponentsInChildren(visibleItems,
                item => item != null && !item.Slot.IsDestroyed && item.Slot.ActiveSelf);

            // Sort items - LINQ makes this almost pleasant
            switch (sortMethod)
            {
                case SortMethod.AToZ:
                    visibleItems = visibleItems.OrderBy(item => item.ItemName).ToList();
                    break;
                case SortMethod.ZToA:
                    visibleItems = visibleItems.OrderByDescending(item => item.ItemName).ToList();
                    break;
                case SortMethod.RecentlyAdded:
                    visibleItems = SortByRecentlyAdded(visibleItems);
                    break;
                case SortMethod.OldestFirst:
                    visibleItems = SortByOldestFirst(visibleItems);
                    break;
                case SortMethod.Default:
                default:
                    // Keep original order
                    return;
            }

            for (int i = 0; i < visibleItems.Count; i++)
            {
                visibleItems[i].Slot.OrderOffset = i;
            }

            var dummy = gridLayout.Slot.AddSlot("dummy");
            dummy.Destroy();
        }

        // Helper method to sort by recently added (most recent first)
        private static List<InventoryItemUI> SortByRecentlyAdded(List<InventoryItemUI> items)
        {
            return items.OrderByDescending(item =>
            {
                if (_itemField != null)
                {
                    try
                    {
                        var recordObj = _itemField.GetValue(item);
                        if (recordObj is FrooxEngine.Store.Record record)
                        {
                            return record.LastModificationTime;
                        }
                    }
                    catch
                    {
                        // Silently handle any reflection errors because who needs to know what went wrong
                    }
                }

                return DateTime.MinValue; 
            }).ToList();
        }

        // Helper method to sort by oldest first - why would anyone want this??
        private static List<InventoryItemUI> SortByOldestFirst(List<InventoryItemUI> items)
        {
            return items.OrderBy(item =>
            {
                if (_itemField != null)
                {
                    try
                    {
                        var recordObj = _itemField.GetValue(item);
                        if (recordObj is FrooxEngine.Store.Record record)
                        {
                            return record.LastModificationTime;
                        }
                    }
                    catch
                    {
                        // Silently handle any reflection errors
                    }
                }

                return DateTime.MaxValue; // same code as above but reversed, don't judge me
            }).ToList();
        }
    }
}