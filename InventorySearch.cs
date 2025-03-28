using Elements.Core;
using FrooxEngine.UIX;
using FrooxEngine;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System;
using System.Linq;
using FrooxEngine.Store;
using ResoniteModLoader;

namespace AdvancedInventoryWithStorage
{
    public static class InventorySearchSystem
    {
        // Enums for search/sort
        public enum SearchScope
        {
            All,
            FoldersOnly,
            ItemsOnly
        }

        public enum SortMethod
        {
            Default,
            AToZ,
            ZToA,
            RecentlyAdded,
            OldestFirst
        }

        private static Dictionary<InventoryBrowser, SearchBarData> _searchBars = new Dictionary<InventoryBrowser, SearchBarData>();

        private static ModConfiguration _config;

        private static FieldInfo _itemField;
        private static FieldInfo _directoryField;

        public static void Initialize(ModConfiguration config, Harmony harmony)
        {
            _config = config;

            // Cache reflection fields to avoid repeated lookups
            _itemField = typeof(InventoryItemUI).GetField("Item", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _directoryField = typeof(InventoryItemUI).GetField("Directory", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            // Note: We don't call harmony.PatchAll() here as the main Mod class will handle that
        }

        private static SearchScope CycleThroughScopes(SearchScope currentScope)
        {
            return (SearchScope)(((int)currentScope + 1) % Enum.GetValues(typeof(SearchScope)).Length);
        }

        private static SortMethod CycleThroughSortMethods(SortMethod currentMethod)
        {
            return (SortMethod)(((int)currentMethod + 1) % Enum.GetValues(typeof(SortMethod)).Length);
        }

        private static string GetScopeButtonText(SearchScope scope)
        {
            switch (scope)
            {
                case SearchScope.All: return "All";
                case SearchScope.FoldersOnly: return "Folders";
                case SearchScope.ItemsOnly: return "Items";
                default: return "All";
            }
        }

        public static void PerformSearch(InventoryBrowser browser, SearchBarData searchBarData, string searchText)
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

            // Find all inventory items - this might get expensive with large inventories
            List<InventoryItemUI> items = new List<InventoryItemUI>();
            browser.Slot.GetComponentsInChildren(items);

            // Reset tracking dictionary
            searchBarData.ItemVisibilityState.Clear();

            // Apply search to each item
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
                isFolder = false;
            }

            bool matchesScope = scope == SearchScope.All ||
                                (scope == SearchScope.FoldersOnly && isFolder) ||
                                (scope == SearchScope.ItemsOnly && !isFolder);

            bool matchesSearch = false;

            // Case sensitivity check
            StringComparison comparison = _config.GetValue(Mod.CASE_SENSITIVE)
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
                // Silently swallow errors
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
            if (gridLayout == null) return;

            // Get all visible items
            List<InventoryItemUI> visibleItems = new List<InventoryItemUI>();
            browser.Slot.GetComponentsInChildren(visibleItems,
                item => item != null && !item.Slot.IsDestroyed && item.Slot.ActiveSelf);

            // Sort items
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
                        // Silently handle any reflection errors
                    }
                }

                return DateTime.MinValue;
            }).ToList();
        }

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

                return DateTime.MaxValue;
            }).ToList();
        }

        // State management for the search bar
        public class SearchBarData
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
            // Dictionary to track visibility state
            public Dictionary<InventoryItemUI, bool> ItemVisibilityState = new Dictionary<InventoryItemUI, bool>();

            public void ClearSearch()
            {
                CurrentSearchText = "";
                IsActive = false;

                // Reset visibility
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

        // Harmony patches for the inventory search system
        [HarmonyPatch(typeof(InventoryBrowser), "OnItemSelected")]
        public static class InventoryBrowserItemSelectedPatch
        {
            public static void Postfix(InventoryBrowser __instance, SyncRef<Slot> ____buttonsRoot)
            {
                if (!_config.GetValue(Mod.SEARCH_ENABLED) || ____buttonsRoot.Target == null)
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
                            CurrentSearchScope = _config.GetValue(Mod.DEFAULT_SEARCH_SCOPE),
                            CurrentSortMethod = _config.GetValue(Mod.DEFAULT_SORT_METHOD)
                        };
                        _searchBars[__instance] = searchBarData;
                    }

                    // Add the search bar
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

                    // Create the UI elements
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

            private static void CreateSearchControls(UIBuilder ui, SearchBarData searchBarData, InventoryBrowser browser)
            {
                // Apply Resonite UI style
                RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);

                // Create the scope button (All/Folders/Items)
                CreateScopeButton(ui, searchBarData, browser);

                CreateSortButton(ui, searchBarData, browser);

                // Create the search field
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
                    buttonImage.Tint.Value = new colorX(0.4f, 0.7f, 0.9f, 0.8f); // blue-ish
                }

                var layoutElement = scopeButton.Slot.GetComponent<LayoutElement>();
                if (layoutElement == null)
                    layoutElement = scopeButton.Slot.AttachComponent<LayoutElement>();

                layoutElement.PreferredWidth.Value = 40f;
                layoutElement.PreferredHeight.Value = 30f;

                scopeButton.Label.Size.Value = 14f;
                scopeButton.Label.Color.Value = RadiantUI_Constants.Neutrals.LIGHT;

                scopeButton.LocalPressed += (btn, data) => {
                    searchBarData.CurrentSearchScope = CycleThroughScopes(searchBarData.CurrentSearchScope);
                    scopeButton.Label.Content.Value = GetScopeButtonText(searchBarData.CurrentSearchScope);
                    if (!string.IsNullOrEmpty(searchBarData.CurrentSearchText))
                        PerformSearch(browser, searchBarData, searchBarData.CurrentSearchText);
                };

                searchBarData.ScopeButton = scopeButton;
            }

            private static void CreateSortButton(UIBuilder ui, SearchBarData searchBarData, InventoryBrowser browser)
            {
                ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);
                ui.Style.MinHeight = 30;
                ui.PushStyle();

                var sortButton = ui.Button(searchBarData.CurrentSortMethod.ToString());
                var buttonImage = sortButton.Slot.GetComponentInChildren<Image>();
                if (buttonImage != null)
                {
                    buttonImage.Tint.Value = new colorX(0.6f, 0.4f, 0.9f, 0.8f); // purple-ish
                }

                var layoutElement = sortButton.Slot.GetComponent<LayoutElement>();
                if (layoutElement == null)
                    layoutElement = sortButton.Slot.AttachComponent<LayoutElement>();

                layoutElement.PreferredWidth.Value = 60f;
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
                searchField.Text.ParseRichText.Value = false;

                searchField.Editor.Target.FinishHandling.Value = TextEditor.FinishAction.NullOnWhitespace;

                var layoutElement = searchField.Slot.GetComponent<LayoutElement>();
                if (layoutElement == null)
                    layoutElement = searchField.Slot.AttachComponent<LayoutElement>();

                layoutElement.PreferredWidth.Value = 150f;
                layoutElement.PreferredHeight.Value = 30f;
                layoutElement.FlexibleWidth.Value = 1f; // Make search field fill available space

                // Style the background
                var bgImage = searchField.Slot.GetComponentInChildren<Image>();
                if (bgImage != null)
                {
                    bgImage.Tint.Value = new colorX(0.15f, 0.15f, 0.15f, 0.9f); // dark gray
                }

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

                var clearButton = ui.Button("×"); // unicode X
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

                clearButton.Label.Size.Value = 18f;
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
    }
}