(() => {
  const SERIES_SCOPE = 2;
  const AUDIO_ANY = 'any';
  const SUB_NONE = 'none';
  const SUB_ANY = 'any';
  const SUBTITLE_MODE_DEFAULT = 1;
  const WIDGET_CLASS = 'trackrules-series-widget';

  document.addEventListener('viewshow', onViewShow);

  function onViewShow(event) {
    const view = event.target;
    if (!view || !view.classList || !view.classList.contains('itemDetailPage')) {
      return;
    }

    const params = event.detail && event.detail.params ? event.detail.params : {};
    const itemId = params.id;
    if (!itemId) {
      return;
    }

    const apiClient = getApiClient();
    if (!apiClient || typeof apiClient.getCurrentUserId !== 'function') {
      return;
    }

    const userId = apiClient.getCurrentUserId();
    if (!userId) {
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
      .catch((err) => console.error('[TrackRules] Failed to inspect item', err));
  }

  function getApiClient() {
    return window.ApiClient;
  }

  function renderWidget(view, series, userId) {
    const host = view.querySelector('.detailPagePrimaryContent');
    if (!host) {
      return;
    }

    removeWidget(view);

    const section = document.createElement('section');
    section.className = `detailSection ${WIDGET_CLASS}`;
    section.dataset.seriesId = (series.Id || '').toString();

    const header = document.createElement('h3');
    header.className = 'sectionTitle';
    header.textContent = 'Track Rules';
    section.appendChild(header);

    const description = document.createElement('p');
    description.className = 'sectionSubtitle';
    description.textContent = 'Choose default audio and subtitle tracks for this series.';
    section.appendChild(description);

    const form = document.createElement('div');
    form.className = 'trackrules-widget-fields';

    const audioField = createSelectField('Audio default');
    audioField.select.classList.add('trackrules-audio');
    form.appendChild(audioField.container);

    const subtitleField = createSelectField('Subtitle default');
    subtitleField.select.classList.add('trackrules-subs');
    form.appendChild(subtitleField.container);

    section.appendChild(form);

    const actions = document.createElement('div');
    actions.className = 'trackrules-widget-actions';

    const previewButton = document.createElement('button');
    previewButton.type = 'button';
    previewButton.setAttribute('is', 'emby-button');
    previewButton.className = 'raised button-raised trackrules-btn-preview';
    previewButton.textContent = 'Preview';
    actions.appendChild(previewButton);

    const saveButton = document.createElement('button');
    saveButton.type = 'button';
    saveButton.setAttribute('is', 'emby-button');
    saveButton.className = 'raised button-raised trackrules-btn-save';
    saveButton.textContent = 'Save';
    actions.appendChild(saveButton);

    const status = document.createElement('div');
    status.className = 'trackrules-status';
    status.style.marginTop = '0.5em';
    actions.appendChild(status);

    section.appendChild(actions);

    host.insertBefore(section, host.firstChild);

    section._trackRules = {
      elements: {
        audioSelect: audioField.select,
        subtitleSelect: subtitleField.select,
        previewButton: previewButton,
        saveButton: saveButton,
        status: status,
      },
      state: {
        seriesId: section.dataset.seriesId,
        userId: userId,
      },
    };

    attachEventHandlers(section, series);
    initializeWidget(section, series).catch((err) => {
      console.error('[TrackRules] Failed to initialize widget', err);
      setStatus(section, 'Unable to load Track Rules data.', true);
      setBusy(section, false);
    });
  }

  function createSelectField(labelText) {
    const container = document.createElement('div');
    container.className = 'trackrules-field';

    const label = document.createElement('label');
    label.textContent = labelText;
    container.appendChild(label);

    const select = document.createElement('select');
    select.setAttribute('is', 'emby-select');
    select.className = 'trackrules-select';
    container.appendChild(select);

    return { container, select };
  }

  function removeWidget(view) {
    const existing = view.querySelector(`.${WIDGET_CLASS}`);
    if (existing && existing.parentElement) {
      existing.parentElement.removeChild(existing);
    }
  }

  async function initializeWidget(section, series) {
    setBusy(section, true);
    setStatus(section, 'Loading series languages…');

    const state = section._trackRules.state;
    const apiClient = getApiClient();

    const results = await Promise.all([
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

    const languages = results[0];
    const rules = results[1];

    if (!section.isConnected) {
      return;
    }

    state.languages = languages || { audio: [], subtitles: [] };
    state.userRules = rules || { Rules: [] };
    state.previewItemId = languages && languages.previewItemId ? languages.previewItemId : null;
    state.currentRule = findSeriesRule(state.userRules, state.seriesId);

    populateOptions(section, state);
    setBusy(section, false);

    if (state.previewItemId) {
      setStatus(section, 'Select tracks, preview the change, then save.');
    } else {
      setStatus(section, 'No episode media streams found yet; preview is disabled.');
      section._trackRules.elements.previewButton.disabled = true;
    }
  }

  function populateOptions(section, state) {
    const { audioSelect, subtitleSelect } = section._trackRules.elements;
    const audio = state.languages && Array.isArray(state.languages.audio) ? state.languages.audio : [];
    const subtitles = state.languages && Array.isArray(state.languages.subtitles) ? state.languages.subtitles : [];

    populateSelect(audioSelect, buildAudioOptions(audio));
    populateSelect(subtitleSelect, buildSubtitleOptions(subtitles));

    const existing = state.currentRule;
    const existingAudio = existing && Array.isArray(existing.Audio) ? existing.Audio[0] : (existing && Array.isArray(existing.audio) ? existing.audio[0] : null);
    const existingSubs = existing && Array.isArray(existing.Subs) ? existing.Subs[0] : (existing && Array.isArray(existing.subs) ? existing.subs[0] : null);
    const audioValue = normalizeLanguageValue(existingAudio, AUDIO_ANY);
    const subtitleValue = normalizeLanguageValue(existingSubs, SUB_NONE);

    audioSelect.value = audioValue;
    subtitleSelect.value = subtitleValue;
  }

  function populateSelect(select, options) {
    while (select.firstChild) {
      select.removeChild(select.firstChild);
    }

    options.forEach((opt) => {
      const option = document.createElement('option');
      option.value = opt.value;
      option.textContent = opt.label;
      select.appendChild(option);
    });
  }

  function buildAudioOptions(languages) {
    const options = [{ value: AUDIO_ANY, label: 'Best available' }];
    languages.forEach((option) => {
      if (!option || !option.code) {
        return;
      }

      options.push({
        value: option.code,
        label: formatLanguageLabel(option),
      });
    });
    return options;
  }

  function buildSubtitleOptions(languages) {
    const options = [
      { value: SUB_NONE, label: 'No subtitles' },
      { value: SUB_ANY, label: 'Any available' },
    ];

    languages.forEach((option) => {
      if (!option || !option.code) {
        return;
      }

      options.push({
        value: option.code,
        label: formatLanguageLabel(option),
      });
    });

    return options;
  }

  function formatLanguageLabel(option) {
    if (!option) {
      return '';
    }

    const base = option.label || option.code || '';
    if (option.streamCount && option.streamCount > 0) {
      return `${base} (${option.streamCount})`;
    }

    return base;
  }

  function normalizeLanguageValue(value, fallback) {
    if (!value) {
      return fallback;
    }

    const normalized = value.toString().trim().toLowerCase();
    return normalized || fallback;
  }

  function attachEventHandlers(section, series) {
    const { previewButton, saveButton } = section._trackRules.elements;

    previewButton.addEventListener('click', () => {
      handlePreview(section, series).catch((err) => {
        console.error('[TrackRules] Preview failed', err);
        setStatus(section, 'Preview failed. Check server logs for details.', true);
        setBusy(section, false);
      });
    });

    saveButton.addEventListener('click', () => {
      handleSave(section, series).catch((err) => {
        console.error('[TrackRules] Save failed', err);
        setStatus(section, 'Saving failed. Please retry.', true);
        setBusy(section, false);
      });
    });
  }

  async function handlePreview(section, series) {
    const state = section._trackRules.state;
    if (!state.previewItemId) {
      setStatus(section, 'Preview is unavailable until the series has episodes with media streams.', true);
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
    setStatus(section, 'Saving…');

    const apiClient = getApiClient();
    const nextRule = buildRuleFromSelection(section, series.Id);
    const nextRules = buildRuleCollection(state, nextRule);

    const payload = {
      version: resolveVersion(state.userRules),
      userId: state.userId,
      rules: nextRules,
    };

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
    setStatus(section, 'Series defaults saved successfully.');
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

    filtered.push(nextRule);
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
    const { audioSelect, subtitleSelect } = section._trackRules.elements;
    const state = section._trackRules.state;
    const existing = state.currentRule || {};

    const audioSelection = normalizeLanguageValue(audioSelect.value, AUDIO_ANY);
    const subtitleSelection = normalizeLanguageValue(subtitleSelect.value, SUB_NONE);

    return {
      Scope: SERIES_SCOPE,
      TargetId: seriesId,
      Audio: [audioSelection],
      Subs: [subtitleSelection],
      SubsMode: existing.SubsMode ?? existing.subsMode ?? SUBTITLE_MODE_DEFAULT,
      DontTranscode: existing.DontTranscode ?? existing.dontTranscode ?? false,
      Enabled: existing.Enabled ?? existing.enabled ?? true,
    };
  }

  function findSeriesRule(ruleSet, seriesId) {
    if (!ruleSet || !Array.isArray(ruleSet.Rules) && !Array.isArray(ruleSet.rules)) {
      return null;
    }

    const rules = Array.isArray(ruleSet.Rules) ? ruleSet.Rules : ruleSet.rules;
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
    const { audioSelect, subtitleSelect, previewButton, saveButton } = section._trackRules.elements;
    [audioSelect, subtitleSelect, previewButton, saveButton].forEach((element) => {
      if (element) {
        element.disabled = !!busy;
      }
    });

    if (!busy) {
      const state = section._trackRules.state;
      if (!state.previewItemId && previewButton) {
        previewButton.disabled = true;
      }
    }
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
      if (result.SubtitleStreamIndex < 0) {
        parts.push('Subtitles → Off');
      } else {
        parts.push(`Subtitles → #${result.SubtitleStreamIndex}`);
      }
    }

    if (parts.length === 0) {
      return 'No changes are required for this selection.';
    }

    return parts.join(' · ');
  }
})();

