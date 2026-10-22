using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NeutralQoL.Core.SettingsTab
{
    internal sealed class QoLMenu : MonoBehaviour
    {
        private const string k_ContentName = "QoLContent";
        private const string k_KeySeparator = " - ";
        private const float k_PanelSpacing = 10f;
        private const float k_OptionIndent = 24f;
        private const float k_LabelGap = 10f;

        private static GameObject s_Holder;
        private static GameObject s_ToggleRow;
        private static GameObject s_SliderRow;
        private static GameObject s_DropdownRow;
        private static GameObject s_PanelTemplate;
        private static GameObject s_ColumnsTemplate;
        private static GameObject s_TitleTemplate;

        private readonly List<Action> m_Refreshers = new List<Action>();
        private RectTransform m_Content;
        private bool m_Dirty;
        private bool m_SaveOnSet;

        public static GameObject CreateTemplate(GameObject sourcePage)
        {
            s_Holder = new GameObject("NeutralQoL.Templates");
            s_Holder.SetActive(false);
            DontDestroyOnLoad(s_Holder);

            // Cloned under an inactive parent so none of the game's components on the page
            // wake up (the Gameplay page reloads PlayerPrefs in OnDestroy).
            var page = Instantiate(sourcePage, s_Holder.transform);
            page.name = "QoLPage";
            // A dropdown's option list is built from a Toggle, so skip those.
            var toggle = page.GetComponentsInChildren<Toggle>(true).First(t => t.GetComponentInParent<Dropdown>(true) == null);
            var slider = page.GetComponentInChildren<Slider>(true);
            var dropdown = page.GetComponentInChildren<Dropdown>(true);
            var scroll = page.GetComponentInChildren<ScrollRect>(true);
            var toggleRow = RowOf(toggle.transform);
            var list = scroll != null && scroll.content != null ? scroll.content : toggleRow.parent;
            // Small uppercase TMP heading of a panel, e.g. "KILL FEED OPTIONS".
            var title = page.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.GetComponentInParent<Selectable>(true) == null && t.transform.parent.GetComponent<VerticalLayoutGroup>() != null);
            var panel = toggleRow.parent;
            var columns = panel != list ? panel.parent : null;

            s_ToggleRow = Detach(toggleRow);
            s_SliderRow = Detach(RowOf(slider.transform));
            // The Gameplay dropdown has no label, so give it a toggle row's label.
            s_DropdownRow = BuildDropdownRow(Detach(RowOf(dropdown.transform)));
            s_TitleTemplate = title != null ? Detach(title.transform) : null;
            s_PanelTemplate = panel != list ? Detach(panel) : null;
            if (s_PanelTemplate != null)
            {
                ClearChildren(s_PanelTemplate.transform);
                // The panel sizes itself. The game's panels all have a fitter, but a copy from
                // another page might not.
                AddFitter(s_PanelTemplate);
            }
            s_ColumnsTemplate = columns != null && columns != list && columns.GetComponent<HorizontalLayoutGroup>() != null ? Detach(columns) : null;
            if (s_ColumnsTemplate != null)
            {
                ClearChildren(s_ColumnsTemplate.transform);
                // The game sizes this container by hand for its two fixed panels.
                var layout = s_ColumnsTemplate.GetComponent<HorizontalLayoutGroup>();
                layout.childAlignment = TextAnchor.UpperCenter;
                AddFitter(s_ColumnsTemplate);
            }

            // The Gameplay scrollbar is the only TopToBottom one, so its handle drags
            // backwards. Vanilla never notices, that page fits on screen.
            var scrollbar = scroll != null ? scroll.verticalScrollbar : null;
            if (scrollbar != null && scrollbar.direction == Scrollbar.Direction.TopToBottom)
                scrollbar.direction = Scrollbar.Direction.BottomToTop;

            list.name = k_ContentName;
            ClearChildren(list);
            var listLayout = list.GetComponent<VerticalLayoutGroup>();
            if (listLayout != null)
            {
                listLayout.spacing = k_PanelSpacing;
                listLayout.childAlignment = TextAnchor.UpperCenter;
            }
            StripGameBehaviours(page);
            ResetEvents(page);
            page.AddComponent<QoLMenu>();
            Plugin.Log.LogInfo($"QoL page built from {sourcePage.name}: rows {s_ToggleRow.name}, {s_SliderRow.name}, {s_DropdownRow.name}, panel {(s_PanelTemplate != null ? s_PanelTemplate.name : "none")}, columns {(s_ColumnsTemplate != null ? s_ColumnsTemplate.name : "none")}, title {(s_TitleTemplate != null ? s_TitleTemplate.name : "none")}");
            return page;
        }

        private static void AddFitter(GameObject target)
        {
            var fitter = target.GetComponent<ContentSizeFitter>();
            if (fitter == null)
                fitter = target.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            // CameraPanel ships its fitter disabled and a hand set 700 height.
            fitter.enabled = true;
            // Spare height would otherwise be spread between the rows.
            var layout = target.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (layout != null)
                layout.childForceExpandHeight = false;
        }

        private static GameObject BuildDropdownRow(GameObject dropdown)
        {
            var row = Instantiate(s_ToggleRow, s_Holder.transform);
            row.name = "DropdownRow";
            var toggle = row.GetComponentInChildren<Toggle>(true);
            var place = (RectTransform)s_SliderRow.GetComponentInChildren<Slider>(true).transform;
            var rect = (RectTransform)dropdown.transform;
            rect.SetParent(row.transform, false);
            rect.anchorMin = place.anchorMin;
            rect.anchorMax = place.anchorMax;
            rect.pivot = place.pivot;
            rect.anchoredPosition = place.anchoredPosition;
            rect.sizeDelta = new Vector2(place.sizeDelta.x, rect.sizeDelta.y);
            DestroyImmediate(toggle.gameObject);
            return row;
        }

        private void Start()
        {
            m_Content = FindDeep(transform, k_ContentName) as RectTransform;
            if (m_Content == null)
            {
                Plugin.Log.LogWarning("QoL page has no row list");
                return;
            }
            // Sliders set values on every drag step; save once when the page closes.
            m_SaveOnSet = Plugin.Cfg.SaveOnConfigSet;
            Plugin.Cfg.SaveOnConfigSet = false;
            Plugin.Cfg.SettingChanged += OnSettingChanged;
            Build();
        }

        private void OnDestroy()
        {
            if (m_Content == null)
                return;
            Plugin.Cfg.SettingChanged -= OnSettingChanged;
            Plugin.Cfg.SaveOnConfigSet = m_SaveOnSet;
            Plugin.Cfg.Save();
        }

        private void LateUpdate()
        {
            // Refresh a frame later, after the registry has applied the change.
            if (!m_Dirty)
                return;
            m_Dirty = false;
            Refresh();
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            m_Dirty = true;
        }

        // Two independent columns, each section to the one with fewer rows so far.
        private void Build()
        {
            var sections = FeatureRegistry.All.Where(f => f.ShowInMenu && f.Owner == null).GroupBy(f => f.Section).ToList();
            if (sections.Count == 0)
            {
                AddTitle("No features yet", s_PanelTemplate != null ? NewRow(s_PanelTemplate, m_Content).transform : m_Content);
                Refresh();
                return;
            }
            var columns = BuildColumns(Math.Min(2, sections.Count));
            var rowCounts = new int[columns.Length];
            foreach (var section in sections)
            {
                int column = 0;
                for (int c = 1; c < columns.Length; c++)
                {
                    if (rowCounts[c] < rowCounts[column])
                        column = c;
                }
                var panel = s_PanelTemplate != null ? NewRow(s_PanelTemplate, columns[column]).transform : columns[column];
                AddTitle(section.Key, panel);
                foreach (var feature in section)
                    AddFeature(feature, panel);
                rowCounts[column] += 1 + section.Sum(f => 1 + f.Options.Count);
            }
            Refresh();
        }

        private Transform[] BuildColumns(int count)
        {
            if (s_ColumnsTemplate == null || s_PanelTemplate == null || count < 2)
                return new Transform[] { m_Content };
            var line = NewRow(s_ColumnsTemplate, m_Content).transform;
            var panelRect = (RectTransform)s_PanelTemplate.transform;
            float panelWidth = panelRect.rect.width > 0f ? panelRect.rect.width : panelRect.sizeDelta.x;
            var columns = new Transform[count];
            for (int i = 0; i < count; i++)
                columns[i] = NewColumn(line, panelWidth);
            return columns;
        }

        private static Transform NewColumn(Transform parent, float width)
        {
            var column = new GameObject("QoLColumn", typeof(RectTransform));
            column.layer = parent.gameObject.layer;
            var rect = (RectTransform)column.transform;
            rect.SetParent(parent, false);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(width, 0f);
            var layout = column.AddComponent<VerticalLayoutGroup>();
            layout.spacing = k_PanelSpacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            column.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return column.transform;
        }

        private void Refresh()
        {
            foreach (var refresh in m_Refreshers)
                refresh();
            // Panels grow and shrink as features are toggled; inner layouts go first so each
            // fitter sees its children's final size.
            RebuildDeep(m_Content);
        }

        private static void RebuildDeep(RectTransform rect)
        {
            foreach (Transform child in rect)
            {
                if (child.gameObject.activeSelf && child.GetComponent<LayoutGroup>() != null)
                    RebuildDeep((RectTransform)child);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }

        private void AddTitle(string title, Transform parent)
        {
            if (s_TitleTemplate == null)
            {
                AddLabelRow(title, parent);
                return;
            }
            var row = NewRow(s_TitleTemplate, parent);
            SetText(row.GetComponent<TMP_Text>(), title.ToUpperInvariant());
        }

        private void AddLabelRow(string title, Transform parent)
        {
            var row = NewRow(s_ToggleRow, parent);
            var toggle = row.GetComponentInChildren<Toggle>(true);
            var label = LabelOf(row, toggle.transform);
            Destroy(toggle.gameObject);
            SetText(label, title);
            SetBold(label);
        }

        // The feature's toggle as a bold header row with its options indented below. Off or
        // broken features collapse to the header.
        private void AddFeature(Feature feature, Transform panel)
        {
            var header = NewRow(s_ToggleRow, panel);
            var toggle = header.GetComponentInChildren<Toggle>(true);
            var label = LabelOf(header, toggle.transform);
            FitLabel(header, label, toggle.transform, 0f);
            SetBold(label);
            toggle.onValueChanged.AddListener(v => feature.Enabled.Value = v);
            var optionRows = feature.Options.Select(option => AddOption(feature, option, panel)).ToArray();
            m_Refreshers.Add(() =>
            {
                toggle.SetIsOnWithoutNotify(feature.Enabled.Value);
                toggle.interactable = feature.State != FeatureState.Broken;
                SetText(label, feature.State == FeatureState.Broken ? $"{feature.Name} (unavailable)" : feature.Name);
                var expanded = feature.State == FeatureState.On;
                foreach (var row in optionRows)
                    row.SetActive(expanded);
            });
        }

        private GameObject AddOption(Feature feature, ConfigEntryBase entry, Transform panel)
        {
            var name = OptionName(feature, entry);
            var type = entry.SettingType;
            var acceptable = entry.Description.AcceptableValues;
            if (type == typeof(bool))
                return AddToggle(entry, name, panel);
            if (type.IsEnum)
                return AddDropdown(entry, name, panel);
            if (acceptable is AcceptableValueRange<int> ints)
                return AddSlider(entry, name, panel, ints.MinValue, ints.MaxValue, true);
            if (acceptable is AcceptableValueRange<float> floats)
                return AddSlider(entry, name, panel, floats.MinValue, floats.MaxValue, false);
            return AddReadOnly(entry, name, panel);
        }

        private GameObject AddToggle(ConfigEntryBase entry, string name, Transform panel)
        {
            var row = NewRow(s_ToggleRow, panel);
            var toggle = row.GetComponentInChildren<Toggle>(true);
            var label = LabelOf(row, toggle.transform);
            FitLabel(row, label, toggle.transform, k_OptionIndent);
            SetText(label, name);
            toggle.onValueChanged.AddListener(v => entry.BoxedValue = v);
            m_Refreshers.Add(() => toggle.SetIsOnWithoutNotify((bool)entry.BoxedValue));
            return row;
        }

        private GameObject AddDropdown(ConfigEntryBase entry, string name, Transform panel)
        {
            var row = NewRow(s_DropdownRow, panel);
            var dropdown = row.GetComponentInChildren<Dropdown>(true);
            var label = LabelOf(row, dropdown.transform);
            FitLabel(row, label, dropdown.transform, k_OptionIndent);
            SetText(label, name);
            var names = Enum.GetNames(entry.SettingType);
            dropdown.ClearOptions();
            dropdown.AddOptions(names.ToList());
            dropdown.onValueChanged.AddListener(i => entry.BoxedValue = Enum.Parse(entry.SettingType, names[i]));
            m_Refreshers.Add(() => dropdown.SetValueWithoutNotify(Math.Max(0, Array.IndexOf(names, entry.BoxedValue.ToString()))));
            return row;
        }

        private GameObject AddSlider(ConfigEntryBase entry, string name, Transform panel, float min, float max, bool whole)
        {
            var row = NewRow(s_SliderRow, panel);
            var slider = row.GetComponentInChildren<Slider>(true);
            var label = LabelOf(row, slider.transform);
            FitLabel(row, label, slider.transform, k_OptionIndent);
            SetText(label, name);
            // The game shows a slider's value on its handle or next to it, inside the slider.
            var valueText = TextsOutside(row, slider.transform).ElementAtOrDefault(1) ?? Texts(slider.gameObject).FirstOrDefault();
            slider.wholeNumbers = whole;
            slider.minValue = min;
            slider.maxValue = max;
            slider.onValueChanged.AddListener(v => entry.BoxedValue = whole ? (object)Mathf.RoundToInt(v) : v);
            m_Refreshers.Add(() =>
            {
                var value = Convert.ToSingle(entry.BoxedValue);
                slider.SetValueWithoutNotify(value);
                // Invariant culture, so a comma decimal locale doesn't show "0,25".
                SetText(valueText, value.ToString(whole ? "0" : "0.##", CultureInfo.InvariantCulture));
            });
            return row;
        }

        // Types with no matching game control (keybinds, free text) are edited in the config file.
        private GameObject AddReadOnly(ConfigEntryBase entry, string name, Transform panel)
        {
            var row = NewRow(s_ToggleRow, panel);
            var toggle = row.GetComponentInChildren<Toggle>(true);
            var label = LabelOf(row, toggle.transform);
            FitLabel(row, label, null, k_OptionIndent);
            Destroy(toggle.gameObject);
            m_Refreshers.Add(() => SetText(label, $"{name}: {DisplayValue(entry)} (config file)"));
            return row;
        }

        private static string DisplayValue(ConfigEntryBase entry)
        {
            var value = entry.GetSerializedValue();
            return string.IsNullOrEmpty(value) ? "not set" : value;
        }

        // Widen the label up to the control so longer names don't wrap.
        private static void FitLabel(GameObject row, Component label, Transform control, float indent)
        {
            if (label == null)
                return;
            var rect = (RectTransform)label.transform;
            var rowRect = (RectTransform)row.transform;
            float rowWidth = rowRect.rect.width > 0f ? rowRect.rect.width : rowRect.sizeDelta.x;
            float right = rowWidth;
            if (control is RectTransform c)
                right = c.anchorMin.x * rowWidth + c.anchoredPosition.x - c.rect.width * c.pivot.x;
            rect.anchoredPosition += new Vector2(indent, 0f);
            float width = right - rect.anchoredPosition.x - k_LabelGap;
            float scale = rect.localScale.x;
            if (width > 0f && scale > 0f)
                rect.sizeDelta = new Vector2(width / scale, rect.sizeDelta.y);
        }

        private static void SetBold(Component label)
        {
            if (label is Text text)
                text.fontStyle = FontStyle.Bold;
            else if (label is TMP_Text tmp)
                tmp.fontStyle = FontStyles.Bold;
        }

        private static GameObject NewRow(GameObject template, Transform parent)
        {
            var row = Instantiate(template, parent);
            row.SetActive(true);
            return row;
        }

        private static string OptionName(Feature feature, ConfigEntryBase entry)
        {
            var key = entry.Definition.Key;
            var prefix = feature.Name + k_KeySeparator;
            return key.StartsWith(prefix, StringComparison.Ordinal) ? key.Substring(prefix.Length) : key;
        }

        // A row is the control's ancestor that sits directly in a layout group.
        private static Transform RowOf(Transform control)
        {
            var row = control;
            while (row.parent != null && row.parent.GetComponent<LayoutGroup>() == null)
                row = row.parent;
            return row.parent != null ? row : control;
        }

        private static GameObject Detach(Transform row)
        {
            row.SetParent(s_Holder.transform, false);
            StripGameBehaviours(row.gameObject);
            ResetEvents(row.gameObject);
            return row.gameObject;
        }

        private static void ClearChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
                DestroyImmediate(parent.GetChild(i).gameObject);
        }

        // Game scripts on a copied page would run the game's own settings logic.
        private static void StripGameBehaviours(GameObject root)
        {
            var gameAssembly = typeof(SettingsMenu).Assembly;
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null && behaviour.GetType().Assembly == gameAssembly)
                    DestroyImmediate(behaviour);
            }
        }

        // Copied controls keep the page's persistent listeners, which call the game's
        // settings scripts and would write its PlayerPrefs; replace them.
        private static void ResetEvents(GameObject root)
        {
            foreach (var toggle in root.GetComponentsInChildren<Toggle>(true))
            {
                toggle.onValueChanged = new Toggle.ToggleEvent();
                toggle.group = null;
            }
            foreach (var slider in root.GetComponentsInChildren<Slider>(true))
                slider.onValueChanged = new Slider.SliderEvent();
            foreach (var dropdown in root.GetComponentsInChildren<Dropdown>(true))
                dropdown.onValueChanged = new Dropdown.DropdownEvent();
            foreach (var button in root.GetComponentsInChildren<Button>(true))
                button.onClick = new Button.ButtonClickedEvent();
            foreach (var trigger in root.GetComponentsInChildren<EventTrigger>(true))
                DestroyImmediate(trigger);
        }

        private static Component LabelOf(GameObject row, Transform control)
        {
            return TextsOutside(row, control).FirstOrDefault() ?? Texts(row).FirstOrDefault();
        }

        private static List<Component> TextsOutside(GameObject row, Transform control)
        {
            return Texts(row).Where(t => !t.transform.IsChildOf(control)).ToList();
        }

        private static IEnumerable<Component> Texts(GameObject row)
        {
            return row.GetComponentsInChildren<Component>(true).Where(c => c is Text || c is TMP_Text);
        }

        private static void SetText(Component label, string value)
        {
            if (label is Text text)
                text.text = value;
            else if (label is TMP_Text tmp)
                tmp.text = value;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name)
                return root;
            foreach (Transform child in root)
            {
                var found = FindDeep(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
