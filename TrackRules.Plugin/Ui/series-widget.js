(() => {
  const SERIES_SCOPE = 2;
  const AUDIO_ANY = 'any';
  const SUB_NONE = 'none';
  const SUB_ANY = 'any';
  const SUBTITLE_MODE_DEFAULT = 1;
  const WIDGET_CLASS = 'trackrules-series-defaults';

  const SUBTITLE_MODES = [
    { value: 1, label: 'Match server default' },
    { value: 2, label: 'Prefer forced tracks' },
    { value: 3, label: 'Always show subtitles' },
    { value: 4, label: 'Only if audio is not preferred' },
    { value: 0, label: 'Never enable subtitles' },
  ];

  document.addEventListener('viewshow', onViewShow);

  function onViewShow(event) {
    const view = event.target;
    if (!isItemDetailView(view)) {
      return;
    }

    const params = event.detail && event.detail.params ? event.detail.params : {};
    const itemId = params.id;
    if (!itemId) {
      removeWidget(view);
      return;
    }

    const apiClient = getApiClient();
    const userId = apiClient && typeof apiClient.getCurrentUserId === 'function'
      ? apiClient.getCurrentUserId()
      : null;

    if (!apiClient || !userId) {
      removeWidget(view);
      return;
    }

    apiClient.getItem(userId, itemId)
      .then((item) => {
        if (!item || item.Type !== 'Series') {
          removeWidget(view);
          return;
        }

        renderWidget(view, item, userId);
      })
      .catch((err) => {
        console.error('[TrackRules] Unable to inspect item', err);
        removeWidget(view);
      });
  }

  function isItemDetailView(view) {
    return !!(view && view.classList && view.classList.contains('itemDetailPage'));
  }

  function getApiClient() {
    return window.ApiClient;
  }

  function renderWidget(view, series, userId) {
    const anchor = view.querySelector('.trackSelections');
    if (!anchor || !anchor.parentElement) {
      return;
    }

    removeWidget(view);

    const section = document.createElement('section');
    section.className = `${WIDGET_CLASS} detailSection`;
    section.dataset.seriesId = (series.Id || '').toString();

    const heading = document.createElement('h3');
    heading.className = 'sectionTitle';
    heading.textContent = 'Series playback defaults';
    section.appendChild(heading);

    const hint = document.createElement('p');
    hint.className = 'sectionSubtitle';
    hint.textContent = 'Choose the audio and subtitle defaults you want applied when this series starts.';
    section.appendChild(hint);

    const form = document.createElement('form');
    form.className = 'trackSelections focuscontainer-x trackrules-form';
    form.addEventListener('submit', (e) => e.preventDefault());
    section.appendChild(form);

    const audioField = createTrackSelect('Audio', 'trackrules-audio');
    const subtitleField = createTrackSelect('Subtitles', 'trackrules-subs');
    form.appendChild(audioField.container);
    form.appendChild(subtitleField.container);

    const behaviorField = createTrackSelect('Subtitle behavior', 'trackrules-subs-mode');
    behaviorField.container.classList.add('trackrules-submode-field');
    form.appendChild(behaviorField.container);

    const guardField = createGuardField();
    form.appendChild(guardField.container);

    const actions = createActionRow();
    form.appendChild(actions.container);

    const status = document.createElement('div');
    status.className = 'trackrules-status';
    form.appendChild(status);

    anchor.parentElement.insertBefore(section, anchor.nextSibling);

    section._trackRules = {
      elements: {
        root: section,
        form: form,
        audioSelect: audioField.select,
        subtitleSelect: subtitleField.select,
        subsModeSelect: behaviorField.select,
        guardToggle: guardField.checkbox,
        previewButton: actions.previewButton,
        saveButton: actions.saveButton,
        resetButton: actions.resetButton,
        status,
      },
      state: {
        seriesId: section.dataset.seriesId,
        userId,
        languages: null,
        userRules: null,
        previewItemId: null,
        currentRule: null,
      },
    };

    attachEventHandlers(section, series);
    initializeWidget(section, series).catch((err) => {
      console.error('[TrackRules] Failed to initialize series defaults', err);
      setStatus(section, 'Unable to load playback defaults for this series.', true);
      setBusy(section, false);
    });
  }

  function createTrackSelect(labelText, className) {
    const container = document.createElement('div');
    container.className = 'selectContainer trackSelectionFieldContainer';

    const select = document.createElement('select');
    select.setAttribute('is', 'emby-select');
    select.className = `detailTrackSelect ${className}`;
    select.setAttribute('label', labelText);
    select.disabled = true;

    container.appendChild(select);
    return { container, select };
  }

  function createGuardField() {
    const container = document.createElement('label');
    container.className = 'trackrules-guard-toggle';

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'trackrules-dont-transcode';

    const span = document.createElement('span');
    span.textContent = "Don't transcode when enforcing this rule";

    container.appendChild(checkbox);
    container.appendChild(span);

    return { container, checkbox };
  }

  function createActionRow() {
    const container = document.createElement('div');
    container.className = 'trackrules-actions';

    const previewButton = createButton('Preview', 'trackrules-btn-preview');
    const saveButton = createButton('Save defaults', 'trackrules-btn-save');
    const resetButton = createButton('Reset to inherit', 'trackrules-btn-reset button-flat');

    container.appendChild(previewButton);
    container.appendChild(saveButton);
    container.appendChild(resetButton);

    return { container, previewButton, saveButton, resetButton };
  }

  function createButton(text, className) {
    const button = document.createElement('button');
    button.type = 'button';
    button.setAttribute('is', 'emby-button');
    button.className = `raised button-raised ${className}`;
    button.textContent = text;
    return button;
  }

  function attachEventHandlers(section, series) {
    const {
      previewButton,
      saveButton,
      resetButton,
    } = section._trackRules.elements;

    previewButton.addEventListener('click', () => {
      handlePreview(section, series).catch((err) => {
        console.error('[TrackRules] Preview failed', err);
        setStatus(section, 'Preview failed. See server logs for details.', true);
        setBusy(section, false);
      });
    });

    saveButton.addEventListener('click', () => {
      handleSave(section, series).catch((err) => {
        console.error('[TrackRules] Save failed', err);
        setStatus(section, 'Failed to save series defaults.', true);
        setBusy(section, false);
      });
    });

    resetButton.addEventListener('click', () => {
      handleReset(section).catch((err) => {
        console.error('[TrackRules] Reset failed', err);
        setStatus(section, 'Unable to reset to inherited defaults.', true);
        setBusy(section, false);
      });
    });
  }

  async function initializeWidget(section, series) {
    setBusy(section, true);
    setStatus(section, 'Loading available tracks…');

    const apiClient = getApiClient();
    const state = section._trackRules.state;

    const [languages, rules] = await Promise.all([
      apiClient.ajax({
        type: 'GET',
        url: apiClient.getUrl(`TrackRules/series/${series.Id}/languages`),
        dataType: 'json',
      }),
      apiClient.ajax({
        type: 'GET',
        url: apiClient.getUrl(`TrackRules/user/${state.userId}`),
        dataType: 'json',
      }),
    ]);

    if (!section.isConnected) {
      return;
    }

    state.languages = languages || {};
    state.userRules = rules || { Rules: [] };
    state.previewItemId = (languages && (languages.PreviewItemId || languages.previewItemId)) || null;
    state.currentRule = findSeriesRule(state.userRules, state.seriesId);

    populateOptions(section, state);

    if (!state.previewItemId) {
      setStatus(section, 'Preview unavailable until an episode has media info.', true);
    } else if (state.currentRule) {
      setStatus(section, 'Per-series defaults are active.');
    } else {
      setStatus(section, 'Using library/global defaults. Pick tracks and save to override.');
    }

    setBusy(section, false);

    if (!state.previewItemId) {
      section._trackRules.elements.previewButton.disabled = true;
    }
  }

  function populateOptions(section, state) {
    const {
      audioSelect,
      subtitleSelect,
      subsModeSelect,
      guardToggle,
    } = section._trackRules.elements;

    const audioOptions = buildAudioOptions(state.languages && state.languages.Audio ? state.languages.Audio : state.languages?.audio);
    const subtitleOptions = buildSubtitleOptions(state.languages && state.languages.Subtitles ? state.languages.Subtitles : state.languages?.subtitles);

    populateSelect(audioSelect, audioOptions, AUDIO_ANY);
    populateSelect(subtitleSelect, subtitleOptions, SUB_NONE);
    populateSelect(subsModeSelect, buildSubtitleModeOptions(), SUBTITLE_MODE_DEFAULT);

    const current = state.currentRule;
    const audioValue = normalizeLanguageValue(
      getRuleListValue(current, 'Audio', AUDIO_ANY),
      AUDIO_ANY);
    const subtitleValue = normalizeLanguageValue(
      getRuleListValue(current, 'Subs', SUB_NONE),
      SUB_NONE);
    const subsModeValue = Number(getRuleField(current, 'SubsMode', SUBTITLE_MODE_DEFAULT));
    const dontTranscode = !!getRuleField(current, 'DontTranscode', false);

    ensureOption(audioSelect, audioValue);
    ensureOption(subtitleSelect, subtitleValue);
    ensureOption(subsModeSelect, subsModeValue.toString());

    audioSelect.value = audioValue;
    subtitleSelect.value = subtitleValue;
    subsModeSelect.value = subsModeValue.toString();
    guardToggle.checked = dontTranscode;

    audioSelect.disabled = false;
    subtitleSelect.disabled = false;
    subsModeSelect.disabled = false;
  }

  function buildAudioOptions(languageOptions) {
    const options = [{ value: AUDIO_ANY, label: 'Best available' }];
    (languageOptions || []).forEach((option) => {
      const code = option && (option.Code || option.code);
      if (!code) {
        return;
      }

      options.push({
        value: code.toLowerCase(),
        label: formatLanguageLabel(option),
      });
    });
    return options;
  }

  function buildSubtitleOptions(languageOptions) {
    const options = [
      { value: SUB_NONE, label: 'Off' },
      { value: SUB_ANY, label: 'Any language' },
    ];

    (languageOptions || []).forEach((option) => {
      const code = option && (option.Code || option.code);
      if (!code) {
        return;
      }

      options.push({
        value: code.toLowerCase(),
        label: formatLanguageLabel(option),
      });
    });

    return options;
  }

  function buildSubtitleModeOptions() {
    return SUBTITLE_MODES.map((mode) => ({
      value: mode.value.toString(),
      label: mode.label,
    }));
  }

  function populateSelect(select, options, fallback) {
    while (select.firstChild) {
      select.removeChild(select.firstChild);
    }

    (options || []).forEach((opt) => {
      const option = document.createElement('option');
      option.value = opt.value != null ? opt.value.toString() : '';
      option.textContent = opt.label || '';
      select.appendChild(option);
    });

    if (!select.options.length && fallback) {
      const placeholder = document.createElement('option');
      placeholder.value = fallback;
      placeholder.textContent = fallback;
      select.appendChild(placeholder);
    }
  }

  function ensureOption(select, value) {
    if (!value) {
      return;
    }

    const exists = Array.from(select.options).some((opt) => opt.value === value.toString());
    if (!exists) {
      const option = document.createElement('option');
      option.value = value.toString();
      option.textContent = value.toString().toUpperCase();
      select.appendChild(option);
    }
  }

  function formatLanguageLabel(option) {
    if (!option) {
      return '';
    }

    const label = option.Label || option.label || option.Code || option.code || '';
    if (option.StreamCount > 1 || option.streamCount > 1) {
      const count = option.StreamCount || option.streamCount;
      return `${label} (${count})`;
    }

    return label;
  }

  function normalizeLanguageValue(value, fallback) {
    if (!value) {
      return fallback;
    }

    return value.toString().trim().toLowerCase() || fallback;
  }

  function getRuleListValue(rule, property, fallback) {
    if (!rule) {
      return fallback;
    }

    const direct = rule[property];
    const lower = rule[property.charAt(0).toLowerCase() + property.slice(1)];
    const source = Array.isArray(direct) ? direct : Array.isArray(lower) ? lower : null;
    if (!source || source.length === 0) {
      return fallback;
    }

    return source[0];
  }

  function getRuleField(rule, property, fallback) {
    if (!rule) {
      return fallback;
    }

    if (Object.prototype.hasOwnProperty.call(rule, property)) {
      return rule[property];
    }

    const altKey = property.charAt(0).toLowerCase() + property.slice(1);
    if (Object.prototype.hasOwnProperty.call(rule, altKey)) {
      return rule[altKey];
    }

    return fallback;
  }

  async function handlePreview(section, series) {
    const state = section._trackRules.state;
    if (!state.previewItemId) {
      setStatus(section, 'No preview item available yet.', true);
      return;
    }

    setBusy(section, true);
    setStatus(section, 'Requesting preview…');

    const overrideRule = buildRuleFromSelection(section, series.Id);
    const apiClient = getApiClient();

    const response = await apiClient.ajax({
      type: 'POST',
      url: apiClient.getUrl('TrackRules/preview'),
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify({
        userId: state.userId,
        itemId: state.previewItemId,
        overrideRule,
      }),
    });

    if (!section.isConnected) {
      return;
    }

    setBusy(section, false);
    setStatus(section, describePreview(response));
  }

  async function handleSave(section, series) {
    const state = section._trackRules.state;
    setBusy(section, true);
    setStatus(section, 'Saving series defaults…');

    const nextRule = buildRuleFromSelection(section, series.Id);
    const nextRules = buildRuleCollection(state, nextRule);
    const payload = {
      version: resolveVersion(state.userRules),
      userId: state.userId,
      rules: nextRules,
    };

    const apiClient = getApiClient();
    const response = await apiClient.ajax({
      type: 'PUT',
      url: apiClient.getUrl(`TrackRules/user/${state.userId}`),
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify(payload),
    });

    if (!section.isConnected) {
      return;
    }

    state.userRules = response || payload;
    state.currentRule = findSeriesRule(state.userRules, state.seriesId);

    setBusy(section, false);
    setStatus(section, 'Series defaults saved.');
  }

  async function handleReset(section) {
    const state = section._trackRules.state;
    if (!state.currentRule) {
      setStatus(section, 'Series already inherits library/global rules.');
      return;
    }

    setBusy(section, true);
    setStatus(section, 'Removing series override…');

    const nextRules = buildRuleCollection(state, null);
    const payload = {
      version: resolveVersion(state.userRules),
      userId: state.userId,
      rules: nextRules,
    };

    const apiClient = getApiClient();
    const response = await apiClient.ajax({
      type: 'PUT',
      url: apiClient.getUrl(`TrackRules/user/${state.userId}`),
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify(payload),
    });

    if (!section.isConnected) {
      return;
    }

    state.userRules = response || payload;
    state.currentRule = null;
    populateOptions(section, state);
    setBusy(section, false);
    setStatus(section, 'Reverted to library/global defaults.');
  }

  function buildRuleCollection(state, nextRule) {
    const ruleSet = state.userRules || {};
    const baseRules = Array.isArray(ruleSet.Rules)
      ? ruleSet.Rules
      : Array.isArray(ruleSet.rules)
        ? ruleSet.rules
        : [];
    const existingRules = baseRules.slice();

    const seriesId = (state.seriesId || '').toString().toLowerCase();
    const filtered = existingRules.filter((rule) => {
      const scope = typeof rule.Scope === 'number' ? rule.Scope : rule.scope;
      if (scope !== SERIES_SCOPE) {
        return true;
      }

      const target = (rule.TargetId || rule.targetId || '').toString().toLowerCase();
      return target !== seriesId;
    });

    if (nextRule) {
      filtered.push(nextRule);
    }

    return filtered;
  }

  function resolveVersion(ruleSet) {
    if (!ruleSet) {
      return 1;
    }

    if (typeof ruleSet.Version === 'number') {
      return ruleSet.Version;
    }

    if (typeof ruleSet.version === 'number') {
      return ruleSet.version;
    }

    return 1;
  }

  function buildRuleFromSelection(section, seriesId) {
    const {
      audioSelect,
      subtitleSelect,
      subsModeSelect,
      guardToggle,
    } = section._trackRules.elements;

    return {
      Scope: SERIES_SCOPE,
      TargetId: seriesId,
      Audio: [normalizeLanguageValue(audioSelect.value, AUDIO_ANY)],
      Subs: [normalizeLanguageValue(subtitleSelect.value, SUB_NONE)],
      SubsMode: Number(subsModeSelect.value || SUBTITLE_MODE_DEFAULT),
      DontTranscode: !!guardToggle.checked,
      Enabled: true,
    };
  }

  function findSeriesRule(ruleSet, seriesId) {
    if (!ruleSet) {
      return null;
    }

    const rules = Array.isArray(ruleSet.Rules) ? ruleSet.Rules : ruleSet.rules;
    if (!Array.isArray(rules)) {
      return null;
    }

    const target = (seriesId || '').toString().toLowerCase();
    return rules.find((rule) => {
      const scope = typeof rule.Scope === 'number' ? rule.Scope : rule.scope;
      if (scope !== SERIES_SCOPE) {
        return false;
      }

      const id = (rule.TargetId || rule.targetId || '').toString().toLowerCase();
      return id === target;
    }) || null;
  }

  function setBusy(section, busy) {
    const {
      audioSelect,
      subtitleSelect,
      subsModeSelect,
      guardToggle,
      previewButton,
      saveButton,
      resetButton,
    } = section._trackRules.elements;

    const inputs = [
      audioSelect,
      subtitleSelect,
      subsModeSelect,
      guardToggle,
      previewButton,
      saveButton,
      resetButton,
    ];

    inputs.forEach((element) => {
      if (element) {
        element.disabled = !!busy;
      }
    });
  }

  function setStatus(section, message, isError = false) {
    const status = section._trackRules.elements.status;
    if (!status) {
      return;
    }

    status.textContent = message || '';
    status.style.color = isError ? '#d32f2f' : '';
  }

  function describePreview(result) {
    if (!result) {
      return 'Preview failed.';
    }

    if (result.Reason && !result.AudioStreamIndex && !result.SubtitleStreamIndex) {
      return result.Reason;
    }

    const parts = [];
    if (result.Reason) {
      parts.push(result.Reason);
    }

    if (typeof result.AudioStreamIndex === 'number') {
      parts.push(`Audio → #${result.AudioStreamIndex}`);
    }

    if (typeof result.SubtitleStreamIndex === 'number') {
      parts.push(result.SubtitleStreamIndex < 0 ? 'Subtitles → Off' : `Subtitles → #${result.SubtitleStreamIndex}`);
    }

    if (!parts.length) {
      return 'No changes will be sent for this selection.';
    }

    return parts.join(' · ');
  }

  function removeWidget(view) {
    const existing = view.querySelector(`.${WIDGET_CLASS}`);
    if (!existing) {
      return;
    }

    if (existing.parentElement) {
      existing.parentElement.removeChild(existing);
    }
  }
})();
