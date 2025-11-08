(() => {
  'use strict';

  const PAGE_ID = 'trackRulesConfigPage';
  const RULE_SCOPE = {
    Global: 0,
    Library: 1,
    Series: 2
  };

  const SUBTITLE_MODE_LABEL = {
    0: 'Never',
    1: 'Default',
    2: 'Prefer forced',
    3: 'Always',
    4: 'Audio fallback'
  };

  const state = {
    users: [],
    libraries: [],
    rules: null,
    selectedUserId: null,
    editingKey: null,
    searchTimer: null
  };

  document.addEventListener('viewshow', (event) => {
    const page = event.target;
    if (!page || page.id !== PAGE_ID) {
      return;
    }

    bindEvents(page);
    initialize(page).catch((err) => {
      console.error('[TrackRules] Failed to initialize config', err);
      setStatus(page.querySelector('.trackrules-user-status'), 'Unable to load users.', true);
    });
  });

  function bindEvents(page) {
    const userSelect = page.querySelector('.trackrules-user-select');
    const refreshButton = page.querySelector('.trackrules-refresh');
    const scopeSelect = page.querySelector('.trackrules-scope');
    const saveButton = page.querySelector('.trackrules-save');
    const resetButton = page.querySelector('.trackrules-reset-form');
    const seriesSearch = page.querySelector('.trackrules-series-search');

    if (page._trackRulesBound) {
      return;
    }

    userSelect.addEventListener('change', () => {
      selectUser(page, userSelect.value);
    });

    refreshButton.addEventListener('click', () => {
      if (state.selectedUserId) {
        loadRuleSet(page, state.selectedUserId);
      }
    });

    scopeSelect.addEventListener('change', () => {
      updateTargetVisibility(page);
    });

    saveButton.addEventListener('click', () => {
      persistEditorRule(page).catch((err) => {
        console.error('[TrackRules] Failed to save rule', err);
        setStatus(page.querySelector('.trackrules-editor-status'), 'Failed to save rule.', true);
      });
    });

    resetButton.addEventListener('click', () => {
      state.editingKey = null;
      page.querySelector('.trackrules-series-id').value = '';
      page.querySelector('.trackrules-series-search').value = '';
      clearSearchResults(page);
      resetForm(page);
    });

    seriesSearch.addEventListener('input', () => {
      const term = seriesSearch.value.trim();
      if (state.searchTimer) {
        clearTimeout(state.searchTimer);
      }

      if (term.length < 3) {
        clearSearchResults(page);
        return;
      }

      state.searchTimer = setTimeout(() => {
        searchSeries(page, term);
      }, 250);
    });

    page._trackRulesBound = true;
  }

  function updateTargetVisibility(page) {
    const scope = Number(page.querySelector('.trackrules-scope').value);
    const libraryField = page.querySelector('.trackrules-target-library');
    const seriesField = page.querySelector('.trackrules-target-series');

    if (scope === RULE_SCOPE.Library) {
      libraryField.classList.remove('hide');
      seriesField.classList.add('hide');
    } else if (scope === RULE_SCOPE.Series) {
      seriesField.classList.remove('hide');
      libraryField.classList.add('hide');
    } else {
      libraryField.classList.add('hide');
      seriesField.classList.add('hide');
    }
  }

  async function initialize(page) {
    const apiClient = getApiClient();
    if (!apiClient) {
      setStatus(page.querySelector('.trackrules-user-status'), 'Jellyfin API unavailable.', true);
      return;
    }

    setStatus(page.querySelector('.trackrules-user-status'), 'Loading users…');

    const [users, libraries] = await Promise.all([
      loadUsers(apiClient),
      loadLibraries(apiClient)
    ]);

    state.users = users;
    state.libraries = libraries;

    populateUserSelect(page);
    populateLibrarySelect(page);
    updateTargetVisibility(page);

    if (!users.length) {
      setStatus(page.querySelector('.trackrules-user-status'), 'No accessible users found.', true);
      return;
    }

    setStatus(page.querySelector('.trackrules-user-status'), 'Select a user to begin.');
  }

  async function loadUsers(apiClient) {
    const currentUser = await fetchCurrentUser(apiClient);
    const isAdmin = Boolean(currentUser && currentUser.Policy && currentUser.Policy.IsAdministrator);

    if (isAdmin && typeof apiClient.getUsers === 'function') {
      try {
        const users = await apiClient.getUsers();
        return normalizeUsers(users);
      } catch (error) {
        console.warn('[TrackRules] Unable to load full user list, falling back to current user.', error);
        return currentUser ? normalizeUsers([currentUser]) : [];
      }
    }

    return currentUser ? normalizeUsers([currentUser]) : [];
  }

  function normalizeUsers(users) {
    if (!Array.isArray(users)) {
      return [];
    }

    return users
      .filter((user) => !(user && user.Policy && user.Policy.IsDisabled))
      .sort((a, b) => resolveUserName(a).localeCompare(resolveUserName(b), undefined, { sensitivity: 'base' }));
  }

  async function fetchCurrentUser(apiClient) {
    const userId = typeof apiClient.getCurrentUserId === 'function' ? apiClient.getCurrentUserId() : null;
    if (!userId) {
      return null;
    }

    if (typeof apiClient.getCurrentUser === 'function') {
      try {
        const current = await apiClient.getCurrentUser();
        if (current) {
          return current;
        }
      } catch (error) {
        console.warn('[TrackRules] getCurrentUser failed, retrying via REST.', error);
      }
    }

    if (typeof apiClient.getJSON === 'function' && typeof apiClient.getUrl === 'function') {
      try {
        return await apiClient.getJSON(apiClient.getUrl(`Users/${userId}`));
      } catch (error) {
        console.warn('[TrackRules] Unable to fetch current user profile.', error);
        return { Id: userId };
      }
    }

    return { Id: userId };
  }

  async function loadLibraries(apiClient) {
    if (!apiClient || typeof apiClient.getItems !== 'function') {
      return [];
    }

    try {
      const userId = typeof apiClient.getCurrentUserId === 'function' ? apiClient.getCurrentUserId() : null;
      if (!userId) {
        return [];
      }

      const response = await apiClient.getItems(userId, {
        includeItemTypes: 'CollectionFolder',
        recursive: false,
        sortBy: 'SortName'
      });

      const items = response && Array.isArray(response.Items) ? response.Items : [];
      return items
        .map((item) => ({ id: item.Id, name: item.Name }))
        .sort((a, b) => (a.name || '').localeCompare(b.name || '', undefined, { sensitivity: 'base' }));
    } catch (error) {
      console.warn('[TrackRules] Failed to load libraries.', error);
      return [];
    }
  }

  function populateUserSelect(page) {
    const select = page.querySelector('.trackrules-user-select');
    select.innerHTML = '';

    if (!Array.isArray(state.users) || !state.users.length) {
      const option = document.createElement('option');
      option.value = '';
      option.textContent = 'No accessible users';
      select.appendChild(option);
      select.disabled = true;
      return;
    }

    select.disabled = false;

    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = 'Select user…';
    select.appendChild(placeholder);

    state.users.forEach((user) => {
      const option = document.createElement('option');
      option.value = resolveUserId(user) || '';
      option.textContent = resolveUserName(user) || option.value;
      select.appendChild(option);
    });

    if (state.users.length === 1) {
      const onlyUserId = resolveUserId(state.users[0]);
      select.value = onlyUserId;
      selectUser(page, onlyUserId);
    }
  }

  function resolveUserId(user) {
    return (user && (user.Id || user.id)) || '';
  }

  function resolveUserName(user) {
    return (user && (user.Name || user.name)) || '';
  }

  function populateLibrarySelect(page) {
    const select = page.querySelector('.trackrules-library');
    select.innerHTML = '';

    if (!state.libraries.length) {
      const option = document.createElement('option');
      option.value = '';
      option.textContent = 'No libraries detected';
      select.appendChild(option);
      select.disabled = true;
      return;
    }

    state.libraries.forEach((lib) => {
      const option = document.createElement('option');
      option.value = lib.id;
      option.textContent = lib.name;
      select.appendChild(option);
    });

    select.disabled = false;
  }

  function selectUser(page, userId) {
    if (!userId) {
      state.selectedUserId = null;
      state.rules = null;
      state.editingKey = null;
      renderRuleList(page);
      resetForm(page);
      setStatus(page.querySelector('.trackrules-user-status'), 'Select a user to begin.');
      return;
    }

    state.selectedUserId = userId;
    loadRuleSet(page, userId);
  }

  async function loadRuleSet(page, userId) {
    const statusTarget = page.querySelector('.trackrules-user-status');
    setStatus(statusTarget, 'Loading rules…');

    const apiClient = getApiClient();
    if (!apiClient) {
      setStatus(statusTarget, 'Jellyfin API unavailable.', true);
      return;
    }

    try {
      const data = await apiClient.ajax({
        type: 'GET',
        url: apiClient.getUrl(`TrackRules/user/${userId}`),
        dataType: 'json'
      });

      state.rules = data || { Rules: [], UserId: userId };
      state.editingKey = null;
      resetForm(page);
      renderRuleList(page);
      setStatus(statusTarget, 'Rules loaded.');
    } catch (error) {
      console.error('[TrackRules] Failed to load rule set', error);
      state.rules = { Rules: [], UserId: userId };
      state.editingKey = null;
      resetForm(page);
      renderRuleList(page);
      setStatus(statusTarget, 'Unable to load rules for this user.', true);
    }
  }

  function renderRuleList(page) {
    const container = page.querySelector('.trackrules-rule-list');

    if (!state.rules || !Array.isArray(state.rules.Rules) || !state.rules.Rules.length) {
      container.innerHTML = '<p>No rules defined yet.</p>';
      return;
    }

    const rules = state.rules.Rules.slice().sort((a, b) => {
      const scopeA = typeof a.Scope === 'number' ? a.Scope : a.scope;
      const scopeB = typeof b.Scope === 'number' ? b.Scope : b.scope;
      return scopeB - scopeA;
    });

    container.innerHTML = '';
    rules.forEach((rule) => {
      const scope = typeof rule.Scope === 'number' ? rule.Scope : rule.scope;
      const card = document.createElement('div');
      card.className = 'trackrules-rule-card';

      const header = document.createElement('header');
      const title = document.createElement('div');
      title.innerHTML = `<strong>${describeScope(scope, rule)}</strong>`;

      const actions = document.createElement('div');
      actions.innerHTML = `
        <button type="button" class="button-flat trackrules-edit" data-key="${getRuleKey(rule)}">Edit</button>
        <button type="button" class="button-flat trackrules-delete" data-key="${getRuleKey(rule)}">Delete</button>
      `;

      header.appendChild(title);
      header.appendChild(actions);

      const body = document.createElement('div');
      body.innerHTML = `
        <div>Audio: <code>${(rule.Audio || rule.audio || []).join(', ')}</code></div>
        <div>Subtitles: <code>${(rule.Subs || rule.subs || []).join(', ')}</code></div>
        <div>Subtitle mode: ${SUBTITLE_MODE_LABEL[rule.SubsMode ?? rule.subsMode ?? 1]}</div>
        <div>Don't transcode: ${(rule.DontTranscode ?? rule.dontTranscode) ? 'Yes' : 'No'}</div>
        <div>Status: ${(rule.Enabled ?? rule.enabled) === false ? 'Disabled' : 'Enabled'}</div>
      `;

      card.appendChild(header);
      card.appendChild(body);
      container.appendChild(card);
    });

    container.querySelectorAll('.trackrules-edit').forEach((btn) => {
      btn.addEventListener('click', () => {
        const key = btn.getAttribute('data-key');
        editRule(page, key);
      });
    });

    container.querySelectorAll('.trackrules-delete').forEach((btn) => {
      btn.addEventListener('click', () => {
        const key = btn.getAttribute('data-key');
        deleteRule(page, key).catch((err) => {
          console.error('[TrackRules] Failed to delete rule', err);
          setStatus(page.querySelector('.trackrules-user-status'), 'Failed to delete rule.', true);
        });
      });
    });
  }

  function describeScope(scope, rule) {
    if (scope === RULE_SCOPE.Series) {
      return `Series · ${rule.TargetName || rule.SeriesName || rule.TargetId || rule.targetId || ''}`;
    }

    if (scope === RULE_SCOPE.Library) {
      const library = state.libraries.find((lib) => lib.id === (rule.TargetId || rule.targetId));
      return `Library · ${(library && library.name) || (rule.TargetId || rule.targetId)}`;
    }

    return 'Global';
  }

  function getRuleKey(rule) {
    const scope = typeof rule.Scope === 'number' ? rule.Scope : rule.scope;
    const target = rule.TargetId || rule.targetId || '';
    return `${scope}:${target}`;
  }

  function editRule(page, key) {
    if (!state.rules || !Array.isArray(state.rules.Rules)) {
      return;
    }

    const rule = state.rules.Rules.find((r) => getRuleKey(r) === key);
    if (!rule) {
      return;
    }

    state.editingKey = key;
    const scopeField = page.querySelector('.trackrules-scope');
    scopeField.value = (rule.Scope ?? rule.scope ?? 0).toString();

    const audioField = page.querySelector('.trackrules-audio');
    const subsField = page.querySelector('.trackrules-subs');
    const subsModeField = page.querySelector('.trackrules-subs-mode');
    const guardField = page.querySelector('.trackrules-dont-transcode');
    const enabledField = page.querySelector('.trackrules-enabled');

    audioField.value = (rule.Audio || rule.audio || []).join(',');
    subsField.value = (rule.Subs || rule.subs || []).join(',');
    subsModeField.value = (rule.SubsMode ?? rule.subsMode ?? 1).toString();
    guardField.checked = !!(rule.DontTranscode ?? rule.dontTranscode);
    enabledField.checked = (rule.Enabled ?? rule.enabled) !== false;

    if ((rule.Scope ?? rule.scope) === RULE_SCOPE.Library) {
      const targetField = page.querySelector('.trackrules-library');
      targetField.value = rule.TargetId || rule.targetId || '';
    } else if ((rule.Scope ?? rule.scope) === RULE_SCOPE.Series) {
      page.querySelector('.trackrules-series-id').value = rule.TargetId || rule.targetId || '';
      page.querySelector('.trackrules-series-search').value = rule.TargetName || rule.SeriesName || '';
    }

    updateTargetVisibility(page);
    setStatus(page.querySelector('.trackrules-editor-status'), 'Editing existing rule.');
  }

  async function deleteRule(page, key) {
    if (!state.rules || !Array.isArray(state.rules.Rules)) {
      return;
    }

    state.rules.Rules = state.rules.Rules.filter((rule) => getRuleKey(rule) !== key);
    await saveRules();
    renderRuleList(page);
    setStatus(page.querySelector('.trackrules-user-status'), 'Rule deleted.');
  }

  function resetForm(page) {
    page.querySelector('.trackrules-scope').value = '0';
    page.querySelector('.trackrules-library').selectedIndex = 0;
    page.querySelector('.trackrules-series-search').value = '';
    page.querySelector('.trackrules-series-id').value = '';
    page.querySelector('.trackrules-audio').value = 'any';
    page.querySelector('.trackrules-subs').value = 'none';
    page.querySelector('.trackrules-subs-mode').value = '1';
    page.querySelector('.trackrules-dont-transcode').checked = false;
    page.querySelector('.trackrules-enabled').checked = true;
    updateTargetVisibility(page);
    clearSearchResults(page);
    setStatus(page.querySelector('.trackrules-editor-status'), '');
  }

  function normalizeList(value, fallback) {
    if (!value) {
      return [fallback];
    }

    const parts = value.split(',')
      .map((part) => part.trim().toLowerCase())
      .filter(Boolean);

    if (!parts.length) {
      return [fallback];
    }

    return parts;
  }

  async function persistEditorRule(page) {
    if (!state.selectedUserId) {
      setStatus(page.querySelector('.trackrules-editor-status'), 'Select a user first.', true);
      return;
    }

    const scope = Number(page.querySelector('.trackrules-scope').value);
    let targetId = null;

    if (scope === RULE_SCOPE.Library) {
      targetId = page.querySelector('.trackrules-library').value || null;
      if (!targetId) {
        setStatus(page.querySelector('.trackrules-editor-status'), 'Choose a library.', true);
        return;
      }
    } else if (scope === RULE_SCOPE.Series) {
      targetId = page.querySelector('.trackrules-series-id').value || null;
      if (!targetId) {
        setStatus(page.querySelector('.trackrules-editor-status'), 'Select a series from search results.', true);
        return;
      }
    }

    const rule = {
      Scope: scope,
      TargetId: targetId,
      Audio: normalizeList(page.querySelector('.trackrules-audio').value, 'any'),
      Subs: normalizeList(page.querySelector('.trackrules-subs').value, 'none'),
      SubsMode: Number(page.querySelector('.trackrules-subs-mode').value || 1),
      DontTranscode: !!page.querySelector('.trackrules-dont-transcode').checked,
      Enabled: !!page.querySelector('.trackrules-enabled').checked
    };

    upsertRule(rule);
    await saveRules();
    renderRuleList(page);
    resetForm(page);
    state.editingKey = null;
    setStatus(page.querySelector('.trackrules-user-status'), 'Rule saved.');
  }

  function upsertRule(rule) {
    if (!state.rules) {
      state.rules = { UserId: state.selectedUserId, Rules: [] };
    }

    if (!Array.isArray(state.rules.Rules)) {
      state.rules.Rules = [];
    }

    const key = `${rule.Scope}:${rule.TargetId || ''}`;
    state.rules.Rules = state.rules.Rules.filter((existing) => getRuleKey(existing) !== key);
    state.rules.Rules.push(rule);
  }

  async function saveRules() {
    const apiClient = getApiClient();
    if (!apiClient) {
      throw new Error('Jellyfin API is unavailable.');
    }

    const payload = {
      version: resolveVersion(state.rules),
      userId: state.selectedUserId,
      rules: state.rules.Rules
    };

    const response = await apiClient.ajax({
      type: 'PUT',
      url: apiClient.getUrl(`TrackRules/user/${state.selectedUserId}`),
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify(payload)
    });

    state.rules = response || payload;
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

  function setStatus(element, message, isError) {
    if (!element) {
      return;
    }

    element.textContent = message || '';
    element.style.color = isError ? '#d32f2f' : '';
  }

  async function searchSeries(page, term) {
    const apiClient = getApiClient();
    if (!apiClient) {
      return;
    }

    try {
      const response = await apiClient.getItems(apiClient.getCurrentUserId(), {
        searchTerm: term,
        includeItemTypes: 'Series',
        limit: 12
      });

      renderSeriesResults(page, response.Items || []);
    } catch (error) {
      console.error('[TrackRules] Series search failed', error);
      clearSearchResults(page);
    }
  }

  function renderSeriesResults(page, items) {
    const container = page.querySelector('.trackrules-series-results');
    container.innerHTML = '';

    if (!items.length) {
      container.classList.add('hide');
      return;
    }

    items.forEach((item) => {
      const entry = document.createElement('div');
      entry.className = 'trackrules-series-result';
      entry.textContent = item.Name;
      entry.dataset.id = item.Id;
      entry.addEventListener('click', () => {
        page.querySelector('.trackrules-series-id').value = item.Id;
        page.querySelector('.trackrules-series-search').value = item.Name;
        clearSearchResults(page);
      });
      container.appendChild(entry);
    });

    container.classList.remove('hide');
  }

  function clearSearchResults(page) {
    const container = page.querySelector('.trackrules-series-results');
    container.classList.add('hide');
    container.innerHTML = '';
  }

  function getApiClient() {
    return window.ApiClient || null;
  }
})();
