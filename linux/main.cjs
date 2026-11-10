// Oinzi for Linux. The Windows build (native/) hosts WebView2 in WinForms; this one hosts Chromium through Electron
// and reuses the same chrome UI (native/chrome/chrome.html), agent client (agent.mjs) and MCP server (mcp.mjs).
// Behaviour follows native/BrowserForm*.cs; comments there explain the reasons behind most rules kept here.
const {
  app, BaseWindow, WebContentsView, ipcMain, session, nativeTheme, dialog, Menu, clipboard, shell, nativeImage,
} = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const crypto = require('node:crypto');
const input = require('./input.cjs');

// ---------- Paths ----------

const ROOT = __dirname;
// Shared with the Windows build; a packaged app carries copies under resources/shared.
const SHARED = fs.existsSync(path.join(ROOT, 'shared')) ? path.join(ROOT, 'shared') : path.join(ROOT, '..', 'native');
const shared = file => path.join(SHARED, file);
const dataDir = process.env.OINZI_DATA_DIR || path.join(app.getPath('appData'), 'Oinzi');
const userFilesDir = path.join(app.getPath('documents'), 'Oinzi');
fs.mkdirSync(dataDir, { recursive: true });
// Chromium keeps its profiles (cookies, storage) here, next to Oinzi's own files.
app.setPath('userData', path.join(dataDir, 'profile'));
app.setName('Oinzi');
const VERSION = require('./package.json').version;

const HOME = 'https://www.google.com/';
const SEARCH_ENGINES = {
  google: 'https://www.google.com/search?q=',
  naver: 'https://search.naver.com/search.naver?query=',
  bing: 'https://www.bing.com/search?q=',
  duckduckgo: 'https://duckduckgo.com/?q=',
};
const IDLE_CHOICES = [0, 5, 10, 15, 30, 60, 120];
const SESSION_ID = /^[A-Za-z0-9_-]{6,64}$/;
const DEFAULT_PROFILE = 'default';
const STOPPED_MESSAGE = 'This session was stopped by the person. Stop working and report what you have done so far.';

const readJson = (file, fallback) => { try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return fallback; } };
const writeJson = (file, value) => {
  try { fs.writeFileSync(file + '.tmp', JSON.stringify(value)); fs.renameSync(file + '.tmp', file); } catch { /* disk full or read-only */ }
};
const clip = (text, max) => (text.length > max ? text.slice(0, max) : text);
const str = value => (value === undefined || value === null ? '' : typeof value === 'string' ? value : String(value));
const flag = value => value === true || str(value).toLowerCase() === 'true';

// ---------- Preferences ----------

const PREF_DEFAULTS = {
  Startup: 'restore', HomePage: HOME, Search: 'google', Theme: 'system', DownloadDir: null, Autofill: true,
  AgentOverlay: true, FollowAgent: true, AgentIdleMinutes: 15, AutoMp4: true, AttachTab: true, FollowUp: 'steer',
  NotifyDone: true, DoneSound: true, CustomInstructions: '', OnboardingDone: false,
};
const prefsFile = path.join(dataDir, 'settings.json');
let prefs = { ...PREF_DEFAULTS, ...readJson(prefsFile, {}) };
const savePrefs = () => writeJson(prefsFile, prefs);
const homeUrl = () => { try { const u = new URL(prefs.HomePage); return /^https?:$/.test(u.protocol) ? u.href : HOME; } catch { return HOME; } };
const searchUrl = query => (SEARCH_ENGINES[prefs.Search] || SEARCH_ENGINES.google) + encodeURIComponent(query);
const downloadDir = () => (prefs.DownloadDir && fs.existsSync(prefs.DownloadDir) ? prefs.DownloadDir : app.getPath('downloads'));

function normalizeUrl(value) {
  value = str(value).trim();
  if (!value) return homeUrl();
  try {
    const url = new URL(value);
    if (['http:', 'https:', 'file:'].includes(url.protocol)) return url.href;
  } catch { /* not absolute */ }
  if (value.toLowerCase() === 'about:blank') return 'about:blank';
  if (/^(localhost|127\.0\.0\.1)/i.test(value)) return 'http://' + value;
  if (!value.includes(' ') && value.includes('.')) return 'https://' + value;
  return searchUrl(value);
}

// ---------- Profiles ----------
// Each profile is its own cookie jar (an Electron partition), so one site can be signed in as several accounts.

const profilesFile = path.join(dataDir, 'profiles.json');
const PROFILE_COLORS = ['#7462ee', '#d97757', '#3f6ff0', '#2f9e6e', '#c2417a', '#c99a1a', '#5b8a9a', '#8a6d5b'];
let profileBook = readJson(profilesFile, { Items: [], Credentials: [] });
if (!Array.isArray(profileBook.Items)) profileBook.Items = [];
if (!profileBook.Items.some(p => p.Id === DEFAULT_PROFILE)) profileBook.Items.unshift({ Id: DEFAULT_PROFILE, Name: '기본', Color: PROFILE_COLORS[0], Note: '' });
const findProfile = idOrName => profileBook.Items.find(p => p.Id === idOrName)
  || profileBook.Items.find(p => p.Name.toLowerCase() === str(idOrName).trim().toLowerCase());
const profileName = id => findProfile(id)?.Name ?? '기본';
const partitionOf = profile => `persist:${profile === DEFAULT_PROFILE ? 'default' : 'p-' + profile}`;
let userProfile = DEFAULT_PROFILE;
const lastWorkspaceTab = new Map();

// ---------- State ----------

let win = null;
let stash = null;
let chromeView = null;
let chromeReady = false;
const tabs = new Map();
const sessions = new Map();
const closedTabs = [];
let activeId = null;
let nextId = 1;
let nextOrder = 1;
let nextSessionOrder = 1;
let paletteOpen = false;
let viewportVisible = true;
let viewportBounds = { x: 268, y: 8, w: 1000, h: 800, r: 10 };
let sidebarCollapsed = false;
let sidebarTarget = false;
let sidebarPreparing = false;
let sidebarMotion = false;
let connectionFile = null;
let quitting = false;
// The AI panel arrives in a later step; until then nothing follows a conversation.
const followAssistant = () => false;
const followedSpace = () => null;
const activityScript = fs.readFileSync(shared('assistant/activity.js'), 'utf8');

const activeTab = () => tabs.get(activeId) ?? null;
const sessionLabel = space => sessions.get(space)?.label ?? space;
const inWorkspace = tab => tab.agent || tab.profile === userProfile;
const tabUrl = tab => (tab.wc.isDestroyed() ? '' : tab.wc.getURL());
const tabTitle = tab => (tab.wc.isDestroyed() ? '' : tab.wc.getTitle());
const isWebUrl = url => /^(https?|file):/i.test(url);

function orderedTabs() {
  const order = tab => sessions.get(tab.space)?.order ?? Number.MAX_SAFE_INTEGER;
  return [...tabs.values()].sort((a, b) => (a.agent - b.agent) || (order(a) - order(b))
    || (a.space < b.space ? -1 : a.space > b.space ? 1 : 0) || (a.order - b.order) || (a.id - b.id));
}

// ---------- Chrome UI bridge ----------

function postChrome(message) {
  if (chromeReady && chromeView && !chromeView.webContents.isDestroyed()) chromeView.webContents.send('chrome-message', message);
}

function chromeState() {
  const active = activeTab();
  const history = active?.wc.navigationHistory;
  return {
    type: 'state',
    active: active?.id ?? null,
    canBack: !!history?.canGoBack(),
    canForward: !!history?.canGoForward(),
    url: active ? tabUrl(active) : '',
    loading: !!active?.loading,
    collapsed: sidebarCollapsed,
    userProfile,
    profiles: profileBook.Items.map(p => ({ id: p.Id, name: p.Name, color: p.Color })),
    recording: [],
    tabs: orderedTabs().filter(inWorkspace).map(tab => ({
      id: tab.id, space: tab.space, label: tab.agent ? sessionLabel(tab.space) : '', agent: tab.agent,
      title: tabTitle(tab), url: tabUrl(tab), favicon: tab.favicon || '',
      loading: tab.loading, busy: tab.pending > 0, profile: tab.profile,
      sessionProfile: tab.agent ? sessions.get(tab.space)?.profile ?? null : null,
    })),
  };
}

// Coalesces bursts of tab events into one state message.
let statePending = false;
function refreshSidebar() {
  if (!chromeReady || statePending) return;
  statePending = true;
  setImmediate(() => { statePending = false; if (win && !win.isDestroyed()) postChrome(chromeState()); });
}

function refreshAddress() {
  const title = activeTab() ? tabTitle(activeTab()) : '';
  if (win && !win.isDestroyed()) win.setTitle(title ? `Oinzi — ${title}` : 'Oinzi');
  refreshSidebar();
}

// The active tab's view sits over the hole the chrome page reports. Chromium stops laying out and painting a view
// that is not in a window (zero viewport, no input acks), so tabs an agent is working in stay in the window too,
// underneath the opaque chrome page, at the same size. Idle background tabs leave the window to save resources.
const AGENT_WARM_MS = 90000;
function placeViews() {
  if (!win || win.isDestroyed()) return;
  const { x, y, w, h, r } = viewportBounds;
  const bounds = { x, y, width: Math.max(1, w), height: Math.max(1, h) };
  const moved = [];
  for (const tab of tabs.values()) {
    if (tab.wc.isDestroyed()) continue;
    const layer = tab.id === activeId && viewportVisible ? 'top'
      : tab.pending > 0 || (tab.agent && Date.now() - tab.lastUsed < AGENT_WARM_MS) ? 'under' : null;
    // A view taken out of the window and put back stays blank on Linux, so views move between parents only when
    // they must and are otherwise shown and hidden in place.
    const parent = layer === 'top' ? win.contentView : layer === 'under' ? stash : tab.parent;
    if (parent && parent !== tab.parent) {
      tab.parent?.removeChildView(tab.view);
      parent.addChildView(tab.view);
      tab.parent = parent;
      moved.push(tab);
    }
    if (layer === 'top') { tab.view.setBounds(bounds); tab.view.setBorderRadius(r); }
    else if (layer === 'under') tab.view.setBounds({ x: 0, y: 0, width: bounds.width, height: bounds.height });
    tab.view.setVisible(!!layer);
    tab.layer = layer;
  }
  // A view that changed parents may not repaint on its own; ask for a fresh frame once it has settled.
  for (const tab of moved) setTimeout(() => { if (!tab.wc.isDestroyed()) tab.wc.invalidate(); }, 30);
}

// Makes sure a tab is laid out at full size before an agent reads or drives it.
async function warm(tab) {
  if (tab.layer) return;
  placeViews();
  await input.delay(60);
}

function showTab(tab) {
  activeId = tab.id;
  followWorkspace();
  placeViews();
  refreshAddress();
  if (!paletteOpen) tab.wc.focus();
}

function followWorkspace() {
  const tab = activeTab();
  if (!tab || tab.agent) return;
  lastWorkspaceTab.set(tab.profile, tab.id);
  if (tab.profile === userProfile) return;
  userProfile = tab.profile;
  postChrome(profilesMessage());
}

function profilesMessage() {
  return {
    type: 'profiles', current: userProfile,
    items: profileBook.Items.map(p => ({
      id: p.Id, name: p.Name, color: p.Color, note: p.Note || '', isDefault: p.Id === DEFAULT_PROFILE, source: null,
      tabs: [...tabs.values()].filter(tab => tab.profile === p.Id).length, logins: [],
    })),
  };
}

async function shotOfActive() {
  const tab = activeTab();
  if (!tab || !viewportVisible || tab.wc.isDestroyed()) return null;
  try {
    const image = await Promise.race([tab.wc.capturePage(), input.delay(1500).then(() => null)]);
    if (!image || image.isEmpty()) return null;
    return 'data:image/jpeg;base64,' + image.toJPEG(85).toString('base64');
  } catch { return null; }
}

async function waitForSidebar() {
  while (sidebarPreparing || sidebarMotion) await input.delay(16);
}

async function animateSidebar(collapsed) {
  sidebarTarget = collapsed;
  if (sidebarMotion) { sidebarCollapsed = collapsed; postChrome({ type: 'sidebar.animate', collapsed }); return; }
  if (sidebarPreparing) return;
  if (paletteOpen || !activeTab()) { sidebarCollapsed = collapsed; refreshSidebar(); return; }
  sidebarPreparing = true;
  const shot = await shotOfActive();
  if (!shot) {
    sidebarPreparing = false;
    sidebarCollapsed = sidebarTarget;
    postChrome({ type: 'sidebar.instant', collapsed: sidebarCollapsed });
    return;
  }
  postChrome({ type: 'sidebar.prepare', shot });
}

async function openPalette(mode) {
  if (!chromeReady) return;
  await waitForSidebar();
  const url = activeTab() ? tabUrl(activeTab()) : '';
  if (paletteOpen) { postChrome({ type: 'palette', mode, url }); return; }
  paletteOpen = true;
  postChrome({ type: 'palette', mode, url, shot: await shotOfActive() });
}

function settingsInfo() {
  return {
    version: VERSION, runtime: `Chromium ${process.versions.chrome} (Electron ${process.versions.electron})`,
    dataDir, filesDir: userFilesDir, mcp: path.join(dataDir, 'mcp.mjs'),
    claude: findTool('claude') ?? '', codex: findTool('codex') ?? '',
  };
}

function prefsMessage() {
  return {
    startup: prefs.Startup, homePage: prefs.HomePage, search: prefs.Search, theme: prefs.Theme, autofill: prefs.Autofill,
    agentOverlay: prefs.AgentOverlay, agentIdleMinutes: String(prefs.AgentIdleMinutes), autoMp4: prefs.AutoMp4, attachTab: prefs.AttachTab,
    followUp: prefs.FollowUp, notifyDone: prefs.NotifyDone, doneSound: prefs.DoneSound, customInstructions: prefs.CustomInstructions,
    followAgent: prefs.FollowAgent, downloadDir: downloadDir(),
  };
}

async function openSettings(section) {
  if (!chromeReady) return;
  await waitForSidebar();
  const shot = paletteOpen ? null : await shotOfActive();
  paletteOpen = true;
  postChrome({ type: 'settings', shot, section, prefs: prefsMessage(), info: settingsInfo() });
  postChrome({ type: 'schedules', items: [], startup: false });
  postChrome(profilesMessage());
}

function updatePref(name, value) {
  value = str(value).trim();
  switch (name) {
    case 'startup': if (value === 'restore' || value === 'home') prefs.Startup = value; else return; break;
    case 'homePage': {
      if (value.includes(' ') && !/^https?:\/\//.test(value)) { postChrome({ type: 'settings.note', text: '홈 페이지에는 주소를 입력하세요.' }); return; }
      const url = normalizeUrl(value || HOME);
      if (!/^https?:/.test(url)) return;
      prefs.HomePage = url;
      break;
    }
    case 'search': if (SEARCH_ENGINES[value]) prefs.Search = value; else return; break;
    case 'theme': if (['system', 'light', 'dark'].includes(value)) { prefs.Theme = value; nativeTheme.themeSource = value; } else return; break;
    case 'downloadDir': if (fs.existsSync(value)) prefs.DownloadDir = value; else return; break;
    case 'autofill': prefs.Autofill = value === 'True'; break;
    case 'agentOverlay': prefs.AgentOverlay = value === 'True'; break;
    case 'followAgent': prefs.FollowAgent = value === 'True'; postChrome({ type: 'ai.follow', enabled: prefs.FollowAgent }); break;
    case 'agentIdleMinutes': if (IDLE_CHOICES.includes(Number(value))) prefs.AgentIdleMinutes = Number(value); else return; break;
    case 'autoMp4': prefs.AutoMp4 = value === 'True'; break;
    case 'attachTab': prefs.AttachTab = value === 'True'; break;
    case 'followUp': if (value === 'steer' || value === 'queue') prefs.FollowUp = value; else return; break;
    case 'notifyDone': prefs.NotifyDone = value === 'True'; break;
    case 'doneSound': prefs.DoneSound = value === 'True'; break;
    case 'customInstructions': prefs.CustomInstructions = clip(value, 4000); break;
    default: return;
  }
  savePrefs();
  postChrome({ type: 'settings.prefs', prefs: prefsMessage() });
}

async function clearBrowsingData(kinds) {
  const selected = str(kinds).split(',').map(k => k.trim()).filter(Boolean);
  const storages = new Set();
  if (selected.includes('cookies')) ['cookies', 'localstorage', 'indexdb', 'websql', 'serviceworkers', 'cachestorage'].forEach(s => storages.add(s));
  if (selected.includes('cache')) storages.add('cachestorage');
  const partitions = new Set(profileBook.Items.map(p => partitionOf(p.Id)));
  try {
    for (const partition of partitions) {
      const ses = session.fromPartition(partition);
      if (storages.size) await ses.clearStorageData({ storages: [...storages] });
      if (selected.includes('cache')) await ses.clearCache();
      if (selected.includes('history')) for (const tab of tabs.values()) if (tab.profile && partitionOf(tab.profile) === partition) tab.wc.navigationHistory.clear();
      if (selected.includes('autofill')) await ses.clearStorageData({ storages: ['localstorage'], quotas: [] }).catch(() => {});
    }
    postChrome({ type: 'settings.note', text: '선택한 데이터를 삭제했습니다.' + (selected.includes('cookies') ? ' 사이트에서 로그아웃됐을 수 있습니다.' : '') });
  } catch (error) {
    postChrome({ type: 'settings.note', text: '삭제하지 못했습니다: ' + error.message });
  }
}

function handleSettingsMessage(type, message) {
  switch (type) {
    case 'settings.open': openSettings(str(message.section)); break;
    case 'settings.set': updatePref(str(message.name), message.value); break;
    case 'settings.pickDownload':
      dialog.showOpenDialog(win, { title: '다운로드한 파일을 저장할 폴더', defaultPath: downloadDir(), properties: ['openDirectory', 'createDirectory'] })
        .then(result => { if (!result.canceled && result.filePaths[0]) updatePref('downloadDir', result.filePaths[0]); });
      break;
    case 'settings.clear': clearBrowsingData(message.kinds); break;
    case 'settings.reveal': {
      const target = { data: dataDir, files: userFilesDir, downloads: downloadDir() }[str(message.what)];
      if (target && fs.existsSync(target)) shell.openPath(target);
      break;
    }
    case 'settings.copyMcp':
      clipboard.writeText(`${str(message.engine) === 'codex' ? 'codex' : 'claude'} mcp add oinzi -- node "${path.join(dataDir, 'mcp.mjs')}"`);
      postChrome({ type: 'settings.note', text: '등록 명령을 복사했습니다. 터미널에 붙여 넣어 실행하세요.' });
      break;
  }
}

function handleProfileMessage(type, message) {
  const id = str(message.id);
  switch (type) {
    case 'prof.use': if (findProfile(id)) switchWorkspace(findProfile(id).Id); break;
    case 'prof.newTab':
      if (findProfile(id)) {
        userProfile = findProfile(id).Id;
        postChrome({ type: 'settings.close' });
        createTab(homeUrl(), 'user', true, { profile: userProfile });
      }
      break;
    case 'prof.add': {
      const name = clip(str(message.name).trim(), 30);
      if (!name) break;
      const newId = crypto.randomBytes(4).toString('hex');
      profileBook.Items.push({ Id: newId, Name: name, Color: PROFILE_COLORS[profileBook.Items.length % PROFILE_COLORS.length], Note: '' });
      writeJson(profilesFile, profileBook);
      break;
    }
    case 'prof.rename': {
      const profile = findProfile(id);
      const name = clip(str(message.name).trim(), 30);
      if (profile && name) { profile.Name = name; writeJson(profilesFile, profileBook); }
      break;
    }
    case 'prof.note': {
      const profile = findProfile(id);
      if (profile) { profile.Note = clip(str(message.note).trim(), 200); writeJson(profilesFile, profileBook); }
      break;
    }
    case 'prof.delete': {
      if (id === DEFAULT_PROFILE || !findProfile(id)) break;
      for (const tab of [...tabs.values()].filter(t => t.profile === id)) closeTab(tab);
      session.fromPartition(partitionOf(id)).clearStorageData().catch(() => {});
      profileBook.Items = profileBook.Items.filter(p => p.Id !== id);
      writeJson(profilesFile, profileBook);
      if (userProfile === id) switchWorkspace(DEFAULT_PROFILE);
      break;
    }
    default:
      if (type.startsWith('cred.')) postChrome({ type: 'settings.note', text: '사이트 계정 저장은 리눅스 버전에서 아직 지원하지 않습니다.' });
  }
  postChrome(profilesMessage());
  refreshSidebar();
}

// Shows the tab last used in the profile, or opens one so the workspace is never empty.
function switchWorkspace(profile) {
  userProfile = profile;
  const lastId = lastWorkspaceTab.get(profile);
  const last = tabs.get(lastId);
  const tab = last && !last.agent && last.profile === profile ? last : orderedTabs().find(t => !t.agent && t.profile === profile);
  if (tab) showTab(tab);
  else createTab(homeUrl(), 'user', true, { profile });
}

// ---------- Overview ----------

async function thumbnail(tab) {
  if (tab.wc.isDestroyed()) return null;
  if (tab.thumbAt && Date.now() - tab.thumbAt < 1500) return tab.thumb;
  try {
    const data = await Promise.race([input.capture(tab.wc, { format: 'jpeg', quality: 60 }), input.delay(1200).then(() => null)]);
    if (data) {
      const image = nativeImage.createFromBuffer(Buffer.from(data, 'base64'));
      const small = image.getSize().width > 480 ? image.resize({ width: 480 }) : image;
      tab.thumb = 'data:image/jpeg;base64,' + small.toJPEG(70).toString('base64');
      tab.thumbAt = Date.now();
    }
  } catch { /* tab navigating */ }
  return tab.thumb ?? null;
}

async function overviewCards() {
  const entries = [];
  const several = profileBook.Items.length > 1;
  const groups = new Map();
  for (const tab of orderedTabs().filter(t => !t.agent)) {
    if (!groups.has(tab.profile)) groups.set(tab.profile, []);
    groups.get(tab.profile).push(tab);
  }
  const sortedGroups = [...groups.entries()].sort(([a], [b]) => (b === userProfile) - (a === userProfile));
  for (const [profile, group] of sortedGroups) {
    const active = activeTab();
    const viewing = active && !active.agent && active.profile === profile;
    const mine = viewing ? active : group.find(t => t.id === lastWorkspaceTab.get(profile)) ?? group[0];
    entries.push([mine, thumb => ({
      space: 'user:' + profile, label: several ? profileName(profile) : '내 탭', kind: 'user', engine: '', tabs: group.length,
      title: tabTitle(mine) || '새 탭', current: !!viewing, running: false, status: '', interrupted: false, stopped: false,
      recording: false, profile: null, thumb,
    })]);
  }
  const recent = Date.now() - 45000;
  for (const s of [...sessions.values()].sort((a, b) => a.order - b.order)) {
    const own = [...tabs.values()].filter(t => t.space === s.id);
    const busy = own.some(t => t.pending > 0);
    const running = !s.stopped && (busy || (s.kind !== 'panel' && s.lastSeen > recent));
    if (own.length === 0 && !running) continue;
    const shown = own.find(t => t.pending > 0) ?? own.sort((a, b) => b.lastUsed - a.lastUsed)[0]
      ?? [...s.userTabs].map(id => tabs.get(id)).filter(Boolean).at(-1);
    entries.push([shown, thumb => ({
      space: s.id, label: s.label, kind: s.kind === 'panel' ? 'panel' : 'terminal', engine: '', tabs: own.length,
      title: shown ? tabTitle(shown) : '', current: own.includes(activeTab()), running,
      status: s.stopped ? '중지됨' : busy ? s.activity : s.note || (running ? '터미널에서 작업 중' : ''),
      interrupted: false, stopped: s.stopped, recording: false, profile: several ? profileName(s.profile) : null, thumb,
    })]);
  }
  const thumbs = await Promise.all(entries.map(([tab]) => (tab ? thumbnail(tab) : null)));
  return entries.map(([, card], i) => card(thumbs[i]));
}

async function openOverview() {
  if (!chromeReady) return;
  await waitForSidebar();
  const shot = paletteOpen ? null : await shotOfActive();
  const cards = await overviewCards();
  paletteOpen = true;
  postChrome({ type: 'overview', shot, cards });
}

let overviewBusy = false;
async function refreshOverview() {
  if (overviewBusy || !paletteOpen) return;
  overviewBusy = true;
  try { postChrome({ type: 'overview.cards', cards: await overviewCards() }); }
  finally { overviewBusy = false; }
}

function openSessionTab(space) {
  if (space.startsWith('user:')) { switchWorkspace(findProfile(space.slice(5))?.Id ?? DEFAULT_PROFILE); return; }
  const own = [...tabs.values()].filter(t => t.space === space).sort((a, b) => b.lastUsed - a.lastUsed);
  if (own[0]) showTab(own[0]);
}

function stopSession(space) {
  const s = sessions.get(space);
  if (!s) return;
  s.stopped = true;
  refreshSidebar();
}

// ---------- Chrome messages ----------

function handleChromeMessage(message) {
  const type = str(message?.type);
  if (process.env.OINZI_DEBUG && type !== 'layout') console.log('[chrome→]', type, JSON.stringify(message).slice(0, 120));
  const target = () => tabs.get(Number(message.id));
  if (type.startsWith('settings.')) return handleSettingsMessage(type, message);
  if (type.startsWith('prof.') || type.startsWith('cred.')) return handleProfileMessage(type, message);
  if (type === 'onboarding.closed') { prefs.OnboardingDone = true; savePrefs(); return; }
  switch (type) {
    case 'ready':
      chromeReady = true;
      refreshSidebar();
      postAssistantStatus();
      postChrome({ type: 'settings.prefs', prefs: prefsMessage() });
      postChrome(profilesMessage());
      break;
    case 'layout': placeViewport(message); break;
    case 'theme':
      if (/^#[0-9a-f]{3,8}$/i.test(str(message.canvas))) win?.setBackgroundColor(str(message.canvas));
      break;
    case 'select': if (target()) showTab(target()); break;
    case 'shortcut': handleShortcut({ key: str(message.key), ctrl: flag(message.ctrl), shift: flag(message.shift), alt: flag(message.alt) }); break;
    case 'close': if (target()) closeTab(target()); break;
    case 'open': {
      const url = str(message.url);
      if (flag(message.newTab) || !activeTab()) createTab(url, 'user', true);
      else activeTab().wc.loadURL(normalizeUrl(url)).catch(() => {});
      break;
    }
    case 'back': goBack(); break;
    case 'forward': goForward(); break;
    case 'reload': activeTab()?.wc.reload(); break;
    case 'collapse': animateSidebar(flag(message.collapsed)); break;
    case 'sidebar.ready':
      sidebarPreparing = false;
      sidebarMotion = true;
      viewportVisible = false;
      placeViews();
      sidebarCollapsed = sidebarTarget;
      postChrome({ type: 'sidebar.animate', collapsed: sidebarCollapsed });
      break;
    case 'sidebar.done':
      sidebarMotion = false;
      placeViewport(message);
      if (!paletteOpen) { viewportVisible = true; placeViews(); }
      postChrome({ type: 'sidebar.restored' });
      refreshSidebar();
      break;
    case 'reorder': {
      let order = 0;
      for (const id of message.ids || []) { const tab = tabs.get(Number(id)); if (tab) tab.order = ++order; }
      refreshSidebar();
      break;
    }
    case 'palette': openPalette(str(message.mode)); break;
    case 'overview': openOverview(); break;
    case 'overview.refresh': refreshOverview(); break;
    case 'overview.open': openSessionTab(str(message.space)); break;
    case 'overview.stop': stopSession(str(message.space)); break;
    case 'overview.takeover': stopSession(str(message.space)); openSessionTab(str(message.space)); break;
    case 'frozen':
      viewportVisible = false;
      placeViews();
      chromeView.webContents.focus();
      postChrome({ type: 'focus' });
      break;
    case 'thaw':
      paletteOpen = false;
      viewportVisible = true;
      placeViews();
      activeTab()?.wc.focus();
      break;
    case 'ai.focus': chromeView.webContents.focus(); break;
    case 'ai.blur': if (!paletteOpen) activeTab()?.wc.focus(); break;
    case 'ai.follow': prefs.FollowAgent = flag(message.enabled); savePrefs(); break;
    case 'ai.send':
      postChrome({ type: 'ai', engine: str(message.engine), event: { kind: 'error', text: 'AI 패널은 리눅스 버전에서 다음 단계에 연결됩니다. 지금은 터미널에서 agent.mjs·MCP로 브라우저를 조작할 수 있습니다.' } });
      postChrome({ type: 'ai', engine: str(message.engine), event: { kind: 'done' } });
      break;
    case 'onboarding.open':
    case 'rec.list':
      break;
    default:
      if (process.env.OINZI_DEBUG) console.log('[chrome] unhandled', type);
  }
}

function placeViewport(message) {
  if (sidebarMotion) return;
  const n = key => Math.round(Number(message[key]) || 0);
  viewportBounds = { x: n('x'), y: n('y'), w: Math.max(1, n('w')), h: Math.max(1, n('h')), r: n('r') || 10 };
  placeViews();
}

// ---------- AI tools (status only for now) ----------

function findTool(name) {
  const dirs = (process.env.PATH || '').split(path.delimiter).concat([
    path.join(os.homedir(), '.local', 'bin'), path.join(os.homedir(), '.npm-global', 'bin'), path.join(os.homedir(), '.claude', 'local'),
  ]);
  for (const dir of dirs) {
    const file = path.join(dir, name);
    try { fs.accessSync(file, fs.constants.X_OK); return file; } catch { /* keep looking */ }
  }
  return null;
}

function postAssistantStatus() {
  postChrome({
    type: 'ai.status',
    engines: Object.fromEntries(['claude', 'codex'].map(engine => [engine, { available: !!findTool(engine), running: false, busy: 0 }])),
    runningChats: [],
  });
}

// ---------- Shortcuts ----------

// Key names follow the Windows Forms Keys enum the chrome page already sends ("T", "D1", "Oemcomma"...).
function keyName(code) {
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit[1-9]$/.test(code)) return 'D' + code.slice(5);
  return { Tab: 'Tab', F5: 'F5', ArrowLeft: 'Left', ArrowRight: 'Right', Comma: 'Oemcomma' }[code] ?? null;
}

let lastShortcut = { id: '', at: 0 };
function handleShortcut({ key, ctrl, shift, alt }) {
  const id = `${key}|${ctrl}|${shift}|${alt}`;
  if (lastShortcut.id === id && Date.now() - lastShortcut.at < 80) return true;
  lastShortcut = { id, at: Date.now() };
  const ordered = orderedTabs().filter(inWorkspace);
  const index = ordered.indexOf(activeTab());
  if (ctrl && !alt && key === 'T' && shift) reopenClosedTab();
  else if (ctrl && !alt && key === 'T') openPalette('new');
  else if (ctrl && !alt && !shift && key === 'L') openPalette('current');
  else if (ctrl && !alt && !shift && key === 'K') openPalette('tabs');
  else if (ctrl && !alt && !shift && key === 'W') { if (activeTab()) closeTab(activeTab()); }
  else if (ctrl && !alt && shift && key === 'S') animateSidebar(!sidebarTarget);
  else if (ctrl && !alt && shift && key === 'O') openOverview();
  else if (ctrl && !alt && !shift && key === 'Oemcomma') openSettings('');
  else if (ctrl && !alt && !shift && key === 'J') { chromeView.webContents.focus(); postChrome({ type: 'assist' }); }
  else if (ctrl && !alt && key === 'Tab' && ordered.length > 0)
    showTab(ordered[(index + (shift ? ordered.length - 1 : 1) + ordered.length) % ordered.length]);
  else if (ctrl && !alt && !shift && /^D[1-9]$/.test(key)) {
    const users = ordered.filter(tab => !tab.agent);
    const n = key === 'D9' ? users.length - 1 : Number(key.slice(1)) - 1;
    if (n >= 0 && n < users.length) showTab(users[n]);
  }
  else if ((ctrl && !alt && !shift && key === 'R') || (!ctrl && !alt && key === 'F5')) activeTab()?.wc.reload();
  else if (alt && !ctrl && key === 'Left') goBack();
  else if (alt && !ctrl && key === 'Right') goForward();
  else return false;
  return true;
}

function goBack() { const h = activeTab()?.wc.navigationHistory; if (h?.canGoBack()) h.goBack(); }
function goForward() { const h = activeTab()?.wc.navigationHistory; if (h?.canGoForward()) h.goForward(); }

function reopenClosedTab() {
  const closed = closedTabs.pop();
  if (closed) createTab(closed.url, 'user', true, { profile: findProfile(closed.profile)?.Id ?? DEFAULT_PROFILE });
}

// ---------- Tabs ----------

const configuredSessions = new Set();
function configureSession(ses) {
  if (configuredSessions.has(ses)) return;
  configuredSessions.add(ses);
  // Downloads go straight to the download folder, like other browsers, instead of asking every time.
  ses.on('will-download', (_event, item) => {
    const dir = downloadDir();
    const parsed = path.parse(item.getFilename() || 'download');
    let file = path.join(dir, parsed.base);
    for (let i = 1; fs.existsSync(file); i++) file = path.join(dir, `${parsed.name} (${i})${parsed.ext}`);
    item.setSavePath(file);
    item.once('done', (_e, state) => {
      if (state === 'completed') postChrome({ type: 'toast', text: `다운로드 완료: ${path.basename(file)}` });
    });
  });
}

function createTab(url, space, select, options = {}) {
  const { navigate = true, owner = '', webContents = null } = options;
  const profile = options.profile ?? (space === 'user' ? userProfile : DEFAULT_PROFILE);
  const agent = space !== 'user';
  if (agent && ([...tabs.values()].filter(t => t.agent).length >= 60 || [...tabs.values()].filter(t => t.space === space).length >= 20))
    throw new Error('Agent tab limit reached (20 per space, 60 total). Close unused tabs first.');
  const partition = partitionOf(profile);
  configureSession(session.fromPartition(partition));
  const view = new WebContentsView(webContents ? { webContents } : {
    webPreferences: {
      partition, sandbox: true, contextIsolation: true, nodeIntegration: false,
      preload: path.join(ROOT, 'page-preload.cjs'), nodeIntegrationInSubFrames: true,
      // Agents work in tabs nobody is looking at; throttled timers would stall them.
      backgroundThrottling: !agent,
    },
  });
  view.setBackgroundColor('#ffffff');
  const wc = view.webContents;
  const tab = {
    id: nextId++, space, agent, profile, owner, view, wc, order: nextOrder++, layer: null, parent: null,
    lastUsed: Date.now(), pending: 0, loading: false, favicon: '', dialogs: [],
    lastSnapshotKeys: null, lastSnapshotText: 0, lastSnapshotUrl: '', documentSerial: 0, scriptedDocument: -1,
    thumb: null, thumbAt: 0,
  };
  tabs.set(tab.id, tab);
  wireTab(tab);
  if (select) showTab(tab); else { placeViews(); refreshSidebar(); }
  if (navigate) wc.loadURL(normalizeUrl(url)).catch(() => {});
  return tab;
}

function wireTab(tab) {
  const { wc } = tab;
  wc.on('page-title-updated', () => { if (activeTab() === tab) refreshAddress(); else refreshSidebar(); });
  wc.on('did-start-navigation', details => {
    if (!details.isMainFrame || details.isSameDocument) return;
    tab.documentSerial++;
  });
  wc.on('did-start-loading', () => { tab.loading = true; refreshSidebar(); });
  wc.on('did-stop-loading', () => { tab.loading = false; if (activeTab() === tab) refreshAddress(); else refreshSidebar(); });
  wc.on('did-navigate', () => { tab.favicon = ''; if (activeTab() === tab) refreshAddress(); });
  wc.on('did-navigate-in-page', () => { if (activeTab() === tab) refreshAddress(); });
  wc.on('page-favicon-updated', (_event, favicons) => { tab.favicon = favicons?.[0] ?? ''; refreshSidebar(); });
  wc.on('destroyed', () => { if (tabs.has(tab.id)) closeTab(tab, { destroyed: true }); });
  // A page calling window.close() closes its tab.
  wc.on('close', () => closeTab(tab));
  wc.setWindowOpenHandler(() => {
    // A popup lives in its opener's profile and session so window.opener keeps working.
    if (tab.agent && [...tabs.values()].filter(t => t.space === tab.space).length >= 20) return { action: 'deny' };
    return {
      action: 'allow',
      outlivesOpener: true,
      createWindow: options => {
        const popup = createTab(null, tab.space, !tab.agent, { navigate: false, owner: tab.owner, profile: tab.profile, webContents: options.webContents });
        return popup.wc;
      },
    };
  });
  wc.on('before-input-event', (event, keys) => {
    if (keys.type !== 'keyDown') return;
    const key = keyName(keys.code);
    if (!key) return;
    const ctrl = keys.control || keys.meta;
    const wanted = (ctrl && !keys.alt && (/^[TKLWJRSO]$/.test(key) || key === 'Tab' || key === 'Oemcomma' || /^D[1-9]$/.test(key)))
      || (key === 'F5' && !ctrl && !keys.alt) || (keys.alt && !ctrl && (key === 'Left' || key === 'Right'));
    if (wanted && handleShortcut({ key, ctrl, shift: keys.shift, alt: keys.alt })) event.preventDefault();
  });
  // A "leave site?" box blocks the page until someone answers, and agents cannot answer it.
  wc.on('will-prevent-unload', event => {
    if (tab.agent || tab.pending > 0) {
      if (tab.dialogs.length < 20) tab.dialogs.push('beforeunload: left the page without saving');
      event.preventDefault();
      return;
    }
    const choice = dialog.showMessageBoxSync(win, {
      type: 'warning', buttons: ['나가기', '머무르기'], defaultId: 0, cancelId: 1,
      title: '사이트에서 나갈까요?', message: '사이트에서 나갈까요?', detail: '변경한 내용이 저장되지 않을 수 있습니다.',
    });
    if (choice === 0) event.preventDefault();
  });
  wc.on('context-menu', (_event, params) => showContextMenu(tab, params));
}

// alert/confirm/prompt from page-preload.cjs. Agent work accepts them and reports them in the next snapshot; the
// person gets the normal dialog (Electron has no prompt box, so a prompt in the person's tab returns null as before).
ipcMain.on('oinzi-dialog', (event, { type, message, value } = {}) => {
  const tab = [...tabs.values()].find(t => !t.wc.isDestroyed() && t.wc === event.sender);
  if (!tab || !(tab.agent || tab.pending > 0)) { event.returnValue = { handled: type === 'prompt', result: null }; return; }
  if (tab.dialogs.length < 20) tab.dialogs.push(`${type}: ${str(message)}`);
  event.returnValue = { handled: true, result: type === 'confirm' ? true : type === 'prompt' ? str(value) : undefined };
});

function showContextMenu(tab, params) {
  const items = [];
  if (params.linkURL) {
    items.push({ label: '새 탭에서 링크 열기', click: () => createTab(params.linkURL, tab.agent ? tab.space : 'user', false, { profile: tab.profile }) });
    items.push({ label: '링크 주소 복사', click: () => clipboard.writeText(params.linkURL) }, { type: 'separator' });
  }
  if (params.mediaType === 'image' && params.srcURL) {
    items.push({ label: '이미지를 새 탭에서 열기', click: () => createTab(params.srcURL, 'user', false, { profile: tab.profile }) });
    items.push({ label: '이미지 주소 복사', click: () => clipboard.writeText(params.srcURL) }, { type: 'separator' });
  }
  if (params.isEditable) items.push({ role: 'undo', label: '실행 취소' }, { role: 'redo', label: '다시 실행' }, { type: 'separator' },
    { role: 'cut', label: '잘라내기' }, { role: 'copy', label: '복사' }, { role: 'paste', label: '붙여넣기' }, { role: 'selectAll', label: '모두 선택' });
  else if (params.selectionText) {
    items.push({ role: 'copy', label: '복사' });
    items.push({ label: `"${clip(params.selectionText.trim(), 20)}" 검색`, click: () => createTab(searchUrl(params.selectionText.trim()), 'user', true, { profile: tab.profile }) });
  }
  if (items.length) items.push({ type: 'separator' });
  const history = tab.wc.navigationHistory;
  items.push(
    { label: '뒤로', enabled: history.canGoBack(), click: () => history.goBack() },
    { label: '앞으로', enabled: history.canGoForward(), click: () => history.goForward() },
    { label: '새로고침', click: () => tab.wc.reload() },
    { type: 'separator' },
    { label: '검사', click: () => tab.wc.inspectElement(params.x, params.y) },
  );
  Menu.buildFromTemplate(items).popup({ window: win });
}

function closeTab(tab, { destroyed = false } = {}) {
  if (!tabs.has(tab.id)) return;
  if (activeId === tab.id) {
    // Select a neighbour from the sidebar order.
    const ordered = orderedTabs();
    const index = ordered.indexOf(tab);
    const neighbour = other => other.space === tab.space && (tab.agent || other.profile === tab.profile);
    const next = ordered.slice(index + 1).find(neighbour) ?? ordered.slice(0, index).reverse().find(neighbour)
      ?? ordered.find(other => other !== tab && !other.agent) ?? ordered.find(other => other !== tab);
    activeId = next?.id ?? null;
  }
  tabs.delete(tab.id);
  const url = destroyed ? '' : tabUrl(tab);
  if (!tab.agent && /^https?:/.test(url)) closedTabs.push({ url, profile: tab.profile });
  if (tab.parent && win && !win.isDestroyed()) { tab.parent.removeChildView(tab.view); tab.parent = null; tab.layer = null; }
  if (!destroyed && !tab.wc.isDestroyed()) tab.wc.close();
  if (tab.agent && ![...tabs.values()].some(other => other.space === tab.space) && !sessions.get(tab.space)?.stopped) sessions.delete(tab.space);
  if (quitting) return;
  if (activeTab()) showTab(activeTab()); else placeViews();
  refreshSidebar();
  if (tabs.size === 0) createTab(homeUrl(), 'user', true);
}

function cleanupIdleTabs() {
  if (prefs.AgentIdleMinutes <= 0) return;
  const cutoff = Date.now() - prefs.AgentIdleMinutes * 60000;
  const groups = new Map();
  for (const tab of tabs.values()) if (tab.agent) (groups.get(tab.space) ?? groups.set(tab.space, []).get(tab.space)).push(tab);
  for (const group of groups.values()) {
    if (group.some(tab => tab.pending > 0 || tab.lastUsed > cutoff || tab === activeTab())) continue;
    for (const tab of group) closeTab(tab);
  }
}

// ---------- Saved tabs ----------

const tabsFile = path.join(dataDir, 'tabs.json');

function saveTabs() {
  const saved = orderedTabs().map(tab => ({ url: tabUrl(tab), space: tab.space, active: tab.id === activeId, profile: tab.profile }))
    .filter(tab => isWebUrl(tab.url));
  const savedSessions = [...sessions.values()].filter(s => saved.some(tab => tab.space === s.id)).sort((a, b) => a.order - b.order)
    .map(s => ({ id: s.id, label: s.label, kind: s.kind, profile: s.profile }));
  writeJson(tabsFile, { tabs: saved, sessions: savedSessions });
}

function readSaved() {
  const root = readJson(tabsFile, null);
  if (!root) return { tabs: [], sessions: [] };
  const all = (Array.isArray(root) ? root.map(url => ({ url, space: 'user' })) : root.tabs || [])
    .map(tab => ({ url: str(tab.url), space: str(tab.space) || 'user', active: !!tab.active, profile: str(tab.profile) || DEFAULT_PROFILE }))
    .filter(tab => isWebUrl(tab.url) && tab.space.length <= 80);
  const users = all.filter(tab => tab.space === 'user').slice(0, 50);
  const perSpace = new Map();
  const agents = all.filter(tab => tab.space !== 'user').filter(tab => {
    const count = perSpace.get(tab.space) ?? 0;
    perSpace.set(tab.space, count + 1);
    return count < 20;
  }).slice(0, 60);
  const savedSessions = (Array.isArray(root) ? [] : root.sessions || [])
    .map(s => ({ id: str(s.id), label: str(s.label), kind: str(s.kind) || 'terminal', profile: str(s.profile) || DEFAULT_PROFILE }))
    .filter(s => s.id && s.id !== 'user' && s.id.length <= 80 && s.label);
  return { tabs: [...users, ...agents], sessions: savedSessions };
}

function restoreSession(id, label, kind, profile) {
  if (!sessions.has(id)) sessions.set(id, newSession(id, label, kind, findProfile(profile)?.Id ?? DEFAULT_PROFILE));
}

function newSession(id, label, kind, profile) {
  return { id, label, kind, profile, order: nextSessionOrder++, userTabs: new Set(), lastSeen: 0, activity: '', note: '', stopped: false };
}

function openSession(id, label, kind = 'terminal') {
  let s = sessions.get(id);
  if (!s) {
    if (id === 'user' || id === 'legacy' || !SESSION_ID.test(id)) throw new Error('Invalid session id');
    s = newSession(id, '세션 ' + id.slice(-6), kind, userProfile);
    sessions.set(id, s);
  }
  const name = label ? clip(str(label).trim(), 60) : '';
  if (name && name !== s.label) { s.label = name; refreshSidebar(); }
  return s;
}

// ---------- Agent server ----------

const ACTIVITY_LABELS = {
  click: 'AI가 클릭하고 있어요', fill: 'AI가 입력하고 있어요', scroll: 'AI가 스크롤하고 있어요', goto: 'AI가 페이지를 열고 있어요',
  snapshot: 'AI가 페이지를 읽고 있어요', press: 'AI가 키를 누르고 있어요', wait: 'AI가 페이지를 기다리고 있어요',
  hover: 'AI가 메뉴를 살펴보고 있어요', select: 'AI가 항목을 고르고 있어요', type: 'AI가 입력하고 있어요', clickAt: 'AI가 클릭하고 있어요',
  back: 'AI가 페이지를 옮기고 있어요', forward: 'AI가 페이지를 옮기고 있어요',
};
const activityLabel = command => ACTIVITY_LABELS[command] ?? 'AI가 브라우저를 조작하고 있어요';

async function showActivity(tab, command, target = '', frameId = 'f0', done = false) {
  if (!prefs.AgentOverlay && !done) return;
  const label = done ? '브라우저 조작 완료' : activityLabel(command);
  const script = `${activityScript}\nwindow.__oinziActivity(${JSON.stringify(label)},${JSON.stringify(target)},${done},${JSON.stringify(command)})`;
  try {
    const frame = frameId === 'f0' ? tab.wc.mainFrame : input.framesOf(tab).find(([id]) => id === frameId)?.[1];
    await frame?.executeJavaScript(script);
  } catch { /* page navigating */ }
}

const sessionTabs = space => [...tabs.values()].filter(tab => tab.space === space).map(tab => ({
  id: tab.id, agent: tab.agent, space: tab.space, title: tabTitle(tab), url: tabUrl(tab), profile: profileName(tab.profile),
}));

function profilesForAgent() {
  return profileBook.Items.map(p => ({ id: p.Id, name: p.Name, note: p.Note || '', default: p.Id === DEFAULT_PROFILE, logins: [] }));
}

async function navigate(wc, url) {
  try {
    await Promise.race([wc.loadURL(url), input.delay(30000).then(() => { throw new Error('Navigation timed out'); })]);
  } catch (error) {
    // A redirect or a page script that starts another navigation aborts the first one; that is still a success.
    if (!/ERR_ABORTED|\(-3\)/.test(error.message)) throw new Error('Navigation failed: ' + error.message.split('\n')[0]);
    await new Promise(resolve => { if (!wc.isLoading()) resolve(); else wc.once('did-stop-loading', resolve); });
  }
  return wc.getURL();
}

const BATCHABLE = new Set(['list', 'new', 'show', 'goto', 'snapshot', 'click', 'fill', 'hover', 'select', 'clickAt', 'type', 'press',
  'scroll', 'back', 'forward', 'wait', 'js', 'screenshot', 'close', 'status', 'login', 'compare']);

async function batch(body, sessionId, owner) {
  if (!Array.isArray(body.steps)) throw new Error('steps must be an array of { command, ...arguments }');
  const bail = body.bail !== false;
  const results = [];
  for (let index = 0; index < body.steps.length; index++) {
    if (index >= 50) throw new Error('A batch can hold at most 50 steps.');
    const step = body.steps[index];
    if (!step || typeof step !== 'object') throw new Error(`Step ${index} is not an object`);
    const command = str(step.command);
    if (!BATCHABLE.has(command)) throw new Error(`Step ${index}: ${command} cannot run in a batch`);
    try { results.push(await command_({ ...step, session: sessionId, owner })); }
    catch (error) {
      results.push({ error: error.message });
      if (bail) return { ok: false, failedAt: index, results };
    }
  }
  return { ok: true, results };
}

async function command_(body) {
  const command = str(body.command);
  const owner = str(body.owner);
  if (command === 'cleanup-run' || command === 'heartbeat') {
    if (!owner.trim()) throw new Error('Run owner required');
    const owned = [...tabs.values()].filter(tab => tab.agent && tab.owner === owner);
    for (const tab of owned) {
      if (command === 'cleanup-run') { if (!sessions.get(tab.space)?.stopped) closeTab(tab); }
      else tab.lastUsed = Date.now();
    }
    if (command === 'heartbeat' && sessions.get(str(body.session))) sessions.get(str(body.session)).lastSeen = Date.now();
    return { ok: true, count: owned.length };
  }
  if (command === 'release') { const s = sessions.get(str(body.session)); if (s) s.lastSeen = 0; return { ok: true }; }
  if (command === 'shutdown') return { ok: true };
  if (command === 'save') { saveTabs(); return { ok: true }; }

  const requested = str(body.session);
  if (command === 'session') {
    const created = requested.length === 0;
    const opened = openSession(created ? 's' + crypto.randomBytes(6).toString('hex') : requested, str(body.label));
    if (opened.stopped) throw new Error(STOPPED_MESSAGE);
    opened.lastSeen = Date.now();
    const pinned = str(body.profile);
    if (pinned && pinned !== opened.profile) {
      const found = findProfile(pinned);
      if (!found) throw new Error(`Unknown profile "${pinned}". Profiles: ${profileBook.Items.map(p => p.Name).join(', ')}`);
      opened.profile = found.Id;
      refreshSidebar();
    }
    return { session: opened.id, label: opened.label, created, profile: profileName(opened.profile), tabs: sessionTabs(opened.id) };
  }
  if (!requested) throw new Error('Session required. Run through agent.mjs, or set OINZI_SESSION for cli.mjs.');
  const s = openSession(requested);
  if (s.stopped) throw new Error(STOPPED_MESSAGE);
  s.lastSeen = Date.now();
  if (command === 'status') { s.note = clip(str(body.text).trim(), 80); return { ok: true }; }
  if (command === 'batch') return batch(body, s.id, owner);
  if (command === 'list') {
    for (const tab of tabs.values()) if (tab.space === s.id) tab.lastUsed = Date.now();
    return { active: activeTab()?.space === s.id ? activeId : null, tabs: sessionTabs(s.id) };
  }
  if (command === 'active') {
    const active = activeTab();
    if (!active) throw new Error('No active tab');
    if (active.space !== 'user' && active.space !== s.id) throw new Error("The person is looking at another session's tab");
    if (active.space === 'user') s.userTabs.add(active.id);
    return { id: active.id, space: active.space, title: tabTitle(active), url: tabUrl(active), profile: profileName(active.profile) };
  }
  if (command === 'profiles') return { profiles: profilesForAgent(), sessionProfile: profileName(s.profile) };
  if (command === 'new') {
    const wanted = str(body.profile);
    const profile = !wanted ? findProfile(s.profile)?.Id ?? DEFAULT_PROFILE : findProfile(wanted)?.Id;
    if (!profile) throw new Error(`Unknown profile "${wanted}". Profiles: ${profileBook.Items.map(p => p.Name).join(', ')}`);
    const created = createTab(str(body.url), s.id, false, { owner, profile });
    return { id: created.id, profile: profileName(profile) };
  }
  const tab = tabs.get(parseInt(str(body.id), 10));
  if (!tab) throw new Error('Unknown tab');
  const userTab = tab.space === 'user';
  if (userTab ? !s.userTabs.has(tab.id) : tab.space !== s.id) throw new Error('Tab belongs to another session');
  if (command === 'close') {
    if (userTab) throw new Error("The person's tabs cannot be closed by an agent");
    closeTab(tab);
    return { ok: true };
  }
  if (tab.wc.isDestroyed()) throw new Error('Tab is not ready');
  const followed = followAssistant() && s.id === followedSpace();
  tab.lastUsed = Date.now();
  tab.pending++;
  refreshSidebar();
  try {
    await warm(tab);
    if (command !== 'screenshot') {
      s.activity = activityLabel(command);
      if (followed && !userTab && activeId !== tab.id) showTab(tab);
      await showActivity(tab, command, command === 'press' ? ':focus' : '');
    }
    const target = str(body.target);
    switch (command) {
      case 'show': return { id: tab.id, shown: followed };
      case 'goto': return { url: await navigate(tab.wc, normalizeUrl(body.url)) };
      case 'snapshot': {
        const textLimit = parseInt(str(body.textLimit), 10);
        const elementLimit = parseInt(str(body.elementLimit), 10);
        return input.snapshot(tab,
          Number.isFinite(textLimit) ? Math.min(20000, Math.max(100, textLimit)) : 4000,
          Number.isFinite(elementLimit) ? Math.min(250, Math.max(10, elementLimit)) : 80,
          flag(body.diff));
      }
      case 'click': case 'fill': case 'hover': case 'select': {
        if (!target) throw new Error('Target required');
        const frameId = input.frameIdOf(target), selector = input.selectorOf(target);
        await showActivity(tab, command, selector, frameId);
        if (command === 'click' && activeTab() === tab) await input.delay(90);
        try {
          if (command === 'click') return { ok: true, input: await input.click(tab, target) };
          if (command === 'fill') return { ok: true, input: await input.fill(tab, target, str(body.value)) };
          if (command === 'hover') { await input.hover(tab, target); return { ok: true }; }
          return { ok: true, selected: await input.selectOption(tab, target, str(body.value)) };
        } finally { await showActivity(tab, command, selector, frameId, true); }
      }
      case 'clickAt': {
        const x = Number(body.x), y = Number(body.y);
        if (!Number.isFinite(x) || !Number.isFinite(y)) throw new Error("x and y required (CSS pixels from the viewport's top-left)");
        await input.mouseClick(tab.wc, x, y, flag(body.double) ? 2 : 1);
        return { ok: true };
      }
      case 'type': await input.cdp(tab.wc, 'Input.insertText', { text: str(body.value) }); return { ok: true };
      case 'press': await input.press(tab.wc, str(body.key)); return { ok: true };
      case 'scroll': return { ok: await input.wheel(tab.wc, str(body.direction), str(body.pixels)) };
      case 'back': case 'forward': {
        const history = tab.wc.navigationHistory;
        if (command === 'back' ? !history.canGoBack() : !history.canGoForward()) throw new Error(`Cannot go ${command}`);
        if (command === 'back') history.goBack(); else history.goForward();
        await input.delay(400);
        return { url: tab.wc.getURL() };
      }
      case 'wait': {
        const ms = parseInt(str(body.timeout), 10);
        return { ok: await input.waitFor(tab, target, Number.isFinite(ms) ? ms : 10000) };
      }
      case 'js': {
        tab.scriptedDocument = tab.documentSerial;
        let result;
        try { result = await tab.wc.executeJavaScript(str(body.code)); }
        catch (error) { throw new Error(str(error.message).split('\n')[0] || 'Script failed'); }
        return { result: result === undefined ? null : result };
      }
      case 'screenshot': return { png: await input.capture(tab.wc, { annotate: flag(body.annotate) }) };
      case 'login': throw new Error('login is not available in the Linux build yet. Fill the form with fill/click instead.');
      case 'compare': {
        const threshold = Number(body.threshold);
        return input.compare(tab.wc, str(body.baseline), Number.isFinite(threshold) ? threshold : 0.1, flag(body.diff));
      }
      default: throw new Error(`Unknown command: ${command}`);
    }
  } finally {
    tab.pending--;
    tab.lastUsed = Date.now();
    refreshSidebar();
    if (command !== 'screenshot' && tabs.has(tab.id)) await showActivity(tab, command, '', 'f0', true);
  }
}

function startServer() {
  const token = crypto.randomBytes(32).toString('hex');
  const server = http.createServer((req, res) => {
    const respond = (status, value) => {
      const data = Buffer.from(JSON.stringify(value ?? null));
      res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'content-length': data.length });
      res.end(data);
    };
    if (req.method !== 'POST' || req.headers.authorization !== `Bearer ${token}`) { respond(401, { error: 'Unauthorized' }); return; }
    const chunks = [];
    let size = 0;
    req.on('data', chunk => {
      size += chunk.length;
      if (size > 1024 * 1024) { respond(400, { error: 'Request too large' }); req.destroy(); return; }
      chunks.push(chunk);
    });
    req.on('end', async () => {
      if (res.writableEnded) return;
      try {
        const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
        respond(200, await command_(body));
      } catch (error) {
        respond(400, { error: error.message });
      }
    });
  });
  server.listen(0, '127.0.0.1', () => {
    connectionFile = path.join(dataDir, 'connection.json');
    fs.writeFileSync(connectionFile, JSON.stringify({ port: server.address().port, token }), { mode: 0o600 });
  });
  // Fixed paths for MCP clients and agents; refreshed every start so they match this build.
  for (const file of ['mcp.mjs', 'agent.mjs']) {
    try { fs.copyFileSync(shared(file), path.join(dataDir, file)); } catch { /* read-only data dir */ }
  }
  return server;
}

// ---------- Window ----------

function launchLinks(argv) {
  return argv.slice(1).filter(arg => /^(https?|file):/i.test(arg));
}

async function createWindow() {
  nativeTheme.themeSource = prefs.Theme;
  const dark = nativeTheme.shouldUseDarkColors;
  win = new BaseWindow({
    width: 1380, height: 900, minWidth: 780, minHeight: 520, title: 'Oinzi',
    backgroundColor: dark ? '#131614' : '#ecede7',
    icon: fs.existsSync(path.join(ROOT, 'oinzi.png')) ? path.join(ROOT, 'oinzi.png') : undefined,
  });
  chromeView = new WebContentsView({
    webPreferences: {
      preload: path.join(ROOT, 'chrome-preload.cjs'), contextIsolation: false, sandbox: true, nodeIntegration: false,
      partition: 'oinzi-chrome', spellcheck: false, devTools: process.env.OINZI_CHROME_DEVTOOLS === '1',
    },
  });
  // Transparent so Chromium does not count agent tabs underneath as occluded and stop painting them; the page paints its own background.
  chromeView.setBackgroundColor('#00000000');
  win.contentView.addChildView(chromeView);
  const fit = () => {
    const { width, height } = win.getContentBounds();
    chromeView.setBounds({ x: 0, y: 0, width, height });
  };
  fit();
  win.on('resize', fit);
  // Holds tabs an agent works in while nobody looks at them: a 1×1 window clips them, yet Chromium still counts
  // them as visible, so they keep their full size, paint, and answer input.
  stash = new (require('electron').View)();
  stash.setBounds({ x: 0, y: 0, width: 1, height: 1 });
  win.contentView.addChildView(stash);
  const cwc = chromeView.webContents;
  cwc.on('will-navigate', event => event.preventDefault());
  cwc.setWindowOpenHandler(() => ({ action: 'deny' }));
  cwc.on('console-message', details => {
    if (process.env.OINZI_DEBUG || details.level === 'error') console.log(`[chrome:${details.level}] ${details.message} (${details.lineNumber})`);
  });
  ipcMain.on('chrome-message', (event, message) => {
    if (event.sender !== cwc) return;
    try { handleChromeMessage(message); } catch (error) { console.error('[chrome message]', message?.type, error); }
  });
  await cwc.loadFile(shared('chrome/chrome.html'));
  if (process.env.OINZI_CHROME_DEVTOOLS === '1') cwc.openDevTools({ mode: 'detach' });

  // Restore the previous session's tabs.
  const saved = readSaved();
  for (const s of saved.sessions) restoreSession(s.id, s.label, s.kind, s.profile);
  let restore = saved.tabs;
  if (prefs.Startup === 'home') restore = [{ url: homeUrl(), space: 'user', active: true, profile: DEFAULT_PROFILE }, ...restore.filter(t => t.space !== 'user').map(t => ({ ...t, active: false }))];
  if (restore.length === 0) restore = [{ url: homeUrl(), space: 'user', active: true, profile: DEFAULT_PROFILE }];
  const anyActive = restore.some(t => t.active);
  restore.forEach((t, i) => {
    if (t.space !== 'user') restoreSession(t.space, t.space, 'terminal', t.profile);
    createTab(t.url, t.space, t.active || (!anyActive && i === 0), { profile: findProfile(t.profile)?.Id ?? DEFAULT_PROFILE });
  });
  for (const link of launchLinks(process.argv)) createTab(link, 'user', true);

  const server = startServer();
  const cleanup = setInterval(cleanupIdleTabs, 60000);
  const cool = setInterval(placeViews, 15000);
  const autosave = setInterval(saveTabs, 60000);
  win.on('close', () => {
    quitting = true;
    saveTabs();
    clearInterval(cleanup);
    clearInterval(cool);
    clearInterval(autosave);
    server.close();
    if (connectionFile) try { fs.unlinkSync(connectionFile); } catch { /* already gone */ }
  });
  win.on('closed', () => { win = null; app.quit(); });
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', (_event, argv) => {
    if (!win) return;
    if (win.isMinimized()) win.restore();
    win.focus();
    const links = launchLinks(argv);
    if (links.length) for (const link of links) createTab(link, 'user', true);
  });
  app.on('window-all-closed', () => app.quit());
  app.whenReady().then(() => {
    Menu.setApplicationMenu(null);
    createWindow().catch(error => {
      fs.writeFileSync(path.join(dataDir, 'startup-error.log'), String(error?.stack || error));
      dialog.showErrorBox('Oinzi 시작 오류', String(error?.message || error));
      app.quit();
    });
  });
}
