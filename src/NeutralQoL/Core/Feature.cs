using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace NeutralQoL.Core
{
    public enum FeatureState { Off, On, Broken }

    public abstract class Feature
    {
        public abstract string Section { get; }
        public abstract string Name { get; }
        public abstract string Description { get; }

        // Only pure conveniences ship on; anything that changes what the player sees ships off.
        protected virtual bool DefaultOn => false;
        protected virtual Type[] PatchClasses => Type.EmptyTypes;
        // False hides it from the QoL tab; it stays in the config file.
        public virtual bool ShowInMenu => true;
        // True keeps a feature's code in the build but out of the mod: no config
        // entry, no patches, no ticking. For work put on hold.
        public virtual bool Shelved => false;
        // A part has no toggle in the tab. Its on/off and options live under the owner,
        // and it only runs while the owner is on.
        public virtual Type PartOf => null;
        public Feature Owner { get; private set; }
        internal bool Wanted => Enabled.Value && (Owner == null || Owner.Wanted);

        public FeatureState State { get; private set; }
        public string BrokenReason { get; private set; }
        public ConfigEntry<bool> Enabled { get; private set; }
        public IReadOnlyList<ConfigEntryBase> Options => m_Options;
        internal bool Ticks { get; private set; }

        private readonly List<ConfigEntryBase> m_Options = new List<ConfigEntryBase>();
        private ConfigFile m_Config;
        private PatchSet m_Patches;

        // Finds the game members we need. Runs at startup even when the feature is off,
        // so the log shows everything a game update broke.
        protected virtual void Resolve() { }
        protected virtual void BindOptions() { }
        protected virtual void OnEnable() { }
        // Undo whatever OnEnable or the patches did, so turning it off restores vanilla
        // without a restart.
        protected virtual void OnDisable() { }
        public virtual void Tick() { }

        // inMenu false keeps a rarely changed option in the config file only.
        protected ConfigEntry<T> Bind<T>(string option, T defaultValue, string description, AcceptableValueBase acceptable = null, bool inMenu = true)
        {
            if (Owner != null)
                return Owner.Bind(option, defaultValue, description, acceptable, inMenu);
            var entry = m_Config.Bind(Section, $"{Name} - {option}", defaultValue, new ConfigDescription(description, acceptable));
            if (inMenu)
                m_Options.Add(entry);
            return entry;
        }

        internal void BindEnabled(ConfigFile cfg, Feature owner)
        {
            m_Config = cfg;
            Owner = owner;
            Enabled = owner != null
                ? owner.Bind(Name, DefaultOn, Description)
                : cfg.Bind(Section, Name, DefaultOn, Description);
        }

        internal void Init()
        {
            BindOptions();
            Ticks = GetType().GetMethod(nameof(Tick)).DeclaringType != typeof(Feature);
            try
            {
                Resolve();
            }
            catch (Exception e)
            {
                MarkBroken(e.Message);
            }
        }

        internal void SetActive(bool on)
        {
            if (State == FeatureState.Broken || on == (State == FeatureState.On))
                return;

            if (on)
            {
                m_Patches = new PatchSet($"{Plugin.Guid}.{Section}.{Name}");
                try
                {
                    foreach (var patchClass in PatchClasses)
                        m_Patches.Patch(patchClass);
                    OnEnable();
                    State = FeatureState.On;
                }
                catch (Exception e)
                {
                    TryDisable();
                    m_Patches.UnpatchAll();
                    m_Patches = null;
                    MarkBroken(e.Message);
                }
            }
            else
            {
                TryDisable();
                m_Patches?.UnpatchAll();
                m_Patches = null;
                State = FeatureState.Off;
            }
        }

        internal void Fail(Exception e)
        {
            SetActive(false);
            MarkBroken(e.ToString());
        }

        private void TryDisable()
        {
            try
            {
                OnDisable();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Section}.{Name}: OnDisable failed: {e}");
            }
        }

        private void MarkBroken(string reason)
        {
            State = FeatureState.Broken;
            BrokenReason = reason;
            Plugin.Log.LogWarning($"{Section}.{Name} disabled: {reason}");
        }
    }
}
