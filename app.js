'use strict';

// ───────────────────────── constants & state ─────────────────────────
const STORE_KEY = 'grass-manager-v1';
const DAY_NAMES = ['일', '월', '화', '수', '목', '금', '토'];
const COLORS = ['#2ea043', '#0969da', '#8250df', '#bf3989', '#d1242f', '#bc4c00', '#9a6700', '#1a7f37'];
const SIZE_LABEL = { S: '15분', M: '1시간', L: '반나절' };
// "작은 커밋" 아이디어: 할 일이 없거나 시간이 부족한 날을 위한 최소 단위 커밋
const IDEAS = [
  'README에 사용법/스크린샷 한 줄 추가하기',
  '코드 속 TODO 하나 해결하기',
  '함수 하나 이름/구조 리팩터링하기',
  '테스트 케이스 하나 추가하기',
  '오타·주석 정리하기',
  '의존성 버전 하나 업데이트하기',
  '.gitignore / 설정 파일 정리하기',
  '다음에 할 기능을 이슈(또는 TODO.md)로 정리하기',
  '에러 처리 하나 보강하기',
  '오늘 배운 것을 docs/ 에 메모로 남기기',
];

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const uid = () => Math.random().toString(36).slice(2, 10);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const ymd = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const todayStr = () => ymd(new Date());
const addDays = (d, n) => { const x = new Date(d); x.setDate(x.getDate() + n); return x; };
const daysSince = (iso) => (iso ? Math.floor((Date.now() - new Date(iso).getTime()) / 86400000) : null);

function defaults() {
  return {
    settings: { username: '', token: '', reminderTime: '21:00', notify: false, ntfyTopic: '', lastCall: '23:00', failOnMiss: true },
    projects: [],
    contrib: { fetchedAt: null, days: {}, source: null },
    mission: { date: null, skip: 0 },
    notifiedOn: null,
    tab: 'dashboard',
  };
}
function load() {
  try {
    const s = JSON.parse(localStorage.getItem(STORE_KEY));
    if (s) { const d = defaults(); return { ...d, ...s, settings: { ...d.settings, ...s.settings }, contrib: { ...d.contrib, ...s.contrib } }; }
  } catch {}
  return defaults();
}
let state = load();
function save() { try { localStorage.setItem(STORE_KEY, JSON.stringify(state)); } catch {} }

// ───────────────────────── GitHub ─────────────────────────
// 토큰이 만료·취소되어 GitHub가 거절(401 Bad credentials)하면 이 세션 동안은 토큰 없이 동작한다
let tokenRejected = false;
const activeToken = () => (tokenRejected ? '' : state.settings.token);
function rejectToken() {
  if (tokenRejected) return;
  tokenRejected = true;
  toast('토큰이 만료됐거나 취소됐어요 — 설정에서 새 토큰을 넣어 주세요. 지금은 토큰 없이(최근 90일 공개 활동) 보여 드려요');
}

function ghHeaders() {
  const h = { Accept: 'application/vnd.github+json' };
  if (activeToken()) h.Authorization = `Bearer ${activeToken()}`;
  return h;
}
async function gh(path) {
  const usedToken = !!activeToken();
  const r = await fetch(`https://api.github.com${path}`, { headers: ghHeaders() });
  // 토큰이 거절되면 토큰 없이 한 번 더 시도
  if (r.status === 401 && usedToken) { rejectToken(); return gh(path); }
  if (!r.ok) throw new Error(`GitHub ${r.status}: ${(await r.json().catch(() => ({}))).message || r.statusText}`);
  return r.json();
}

async function graphql(query, variables) {
  const r = await fetch('https://api.github.com/graphql', {
    method: 'POST', headers: { ...ghHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, variables }),
  });
  if (r.status === 401) rejectToken();
  const j = await r.json();
  if (!r.ok || j.errors) throw new Error(j.errors?.[0]?.message || j.message || `GraphQL ${r.status}`);
  return j.data;
}

// 토큰이 있으면 GraphQL로 1년치 잔디(공식 contribution calendar)를 가져온다
async function fetchCalendar(login) {
  const data = await graphql(`query($login:String!){user(login:$login){contributionsCollection{contributionCalendar{totalContributions weeks{contributionDays{date contributionCount}}}}}}`, { login });
  const days = {};
  for (const w of data.user.contributionsCollection.contributionCalendar.weeks)
    for (const d of w.contributionDays) days[d.date] = d.contributionCount;
  return days;
}

// 공개 이벤트(최근 ~90일)로 로컬 시간대 기준 활동을 계산 — 토큰 없을 때의 대체 + "오늘" 판정 보정용
async function fetchEventDays(login) {
  const days = {};
  const repoDays = {}; // 날짜 → 활동한 저장소 목록 (토큰 없을 때 잔디 칸 상세보기에 사용)
  for (let page = 1; page <= 3; page++) {
    const evs = await gh(`/users/${encodeURIComponent(login)}/events${activeToken() ? '' : '/public'}?per_page=100&page=${page}`);
    for (const e of evs) {
      let n = 0;
      if (e.type === 'PushEvent') n = e.payload?.distinct_size ?? e.payload?.size ?? 1;
      else if (e.type === 'PullRequestEvent' && e.payload?.action === 'opened') n = 1;
      else if (e.type === 'IssuesEvent' && e.payload?.action === 'opened') n = 1;
      else if (e.type === 'CreateEvent' && e.payload?.ref_type === 'repository') n = 1;
      if (n) {
        const k = ymd(new Date(e.created_at));
        days[k] = (days[k] || 0) + n;
        const list = (repoDays[k] ||= []);
        let item = list.find((x) => x.name === e.repo.name);
        if (!item) list.push((item = { name: e.repo.name, refs: [] }));
        const ref = e.type === 'PushEvent' && e.payload?.ref?.replace(/^refs\/heads\//, '');
        if (ref && !item.refs.includes(ref)) item.refs.push(ref);
      }
    }
    if (evs.length < 100) break;
  }
  return { days, repoDays };
}

async function fetchRepos() {
  const { username } = state.settings;
  const path = activeToken()
    ? '/user/repos?affiliation=owner&sort=pushed&per_page=100'
    : `/users/${encodeURIComponent(username)}/repos?sort=pushed&per_page=100`;
  return gh(path);
}

// 잔디 칸 상세: 그날 어떤 저장소에 어떤 커밋/PR/이슈를 했는지
const dayCache = {};
async function fetchDayDetail(date) {
  const { username } = state.settings;
  const token = activeToken();
  const from = new Date(`${date}T00:00:00`);
  const to = addDays(from, 1);
  const repos = {};
  const get = (name, extra = {}) => (repos[name] ||= { name, url: `https://github.com/${name}`, private: false, commits: 0, prs: 0, issues: 0, reviews: 0, created: false, messages: null, ...extra });
  let restricted = 0;
  let candidates = [];

  if (token) {
    // exact: 로컬 하루 범위 / wide: 커밋 기여는 날짜 단위로 집계되므로 ±1일로 넓혀 후보 저장소를 찾는다
    const byRepo = (f) => `${f}(maxRepositories:25){repository{nameWithOwner url isPrivate} contributions{totalCount}}`;
    const data = await graphql(`query($login:String!,$from:DateTime!,$to:DateTime!,$wf:DateTime!,$wt:DateTime!){user(login:$login){
      exact: contributionsCollection(from:$from,to:$to){ restrictedContributionsCount
        ${byRepo('commitContributionsByRepository')} ${byRepo('pullRequestContributionsByRepository')}
        ${byRepo('issueContributionsByRepository')} ${byRepo('pullRequestReviewContributionsByRepository')}
        repositoryContributions(first:25){nodes{repository{nameWithOwner url isPrivate}}} }
      wide: contributionsCollection(from:$wf,to:$wt){ ${byRepo('commitContributionsByRepository')} } }}`,
    { login: username, from: from.toISOString(), to: to.toISOString(), wf: addDays(from, -1).toISOString(), wt: addDays(to, 1).toISOString() });
    const ex = data.user.exact;
    restricted = ex.restrictedContributionsCount;
    const meta = (r) => ({ url: r.url, private: r.isPrivate });
    for (const [field, key] of [['pullRequestContributionsByRepository', 'prs'], ['issueContributionsByRepository', 'issues'], ['pullRequestReviewContributionsByRepository', 'reviews']])
      for (const x of ex[field]) get(x.repository.nameWithOwner, meta(x.repository))[key] = x.contributions.totalCount;
    for (const x of ex.repositoryContributions.nodes) get(x.repository.nameWithOwner, meta(x.repository)).created = true;
    const exactCommits = Object.fromEntries(ex.commitContributionsByRepository.map((x) => [x.repository.nameWithOwner, x.contributions.totalCount]));
    candidates = data.user.wide.commitContributionsByRepository.map((x) => ({ name: x.repository.nameWithOwner, meta: meta(x.repository), fallback: exactCommits[x.repository.nameWithOwner] || 0 }));
  } else {
    // 이벤트에는 push한 브랜치가 남아 있으므로 그 브랜치에서 커밋을 찾는다
    candidates = (state.contrib.repoDays?.[date] || []).filter((x) => typeof x === 'object')
      .map((x) => ({ name: x.name, refs: x.refs, meta: {}, fallback: 0 }));
  }

  // 저장소별 실제 커밋 목록 (로컬 하루 범위). 브랜치를 모르면 기본 브랜치 — GitHub 잔디 집계 기준과 같다
  const range = `author=${encodeURIComponent(username)}&since=${from.toISOString()}&until=${to.toISOString()}&per_page=50`;
  await Promise.all(candidates.map(async (c) => {
    try {
      const repo = await gh(`/repos/${c.name}`);
      const branches = [repo.default_branch, ...(c.refs || []).filter((b) => b !== repo.default_branch)];
      const seen = new Map();
      for (const b of branches) {
        const cs = await gh(`/repos/${c.name}/commits?sha=${encodeURIComponent(b)}&${range}`);
        for (const x of cs) if (!seen.has(x.sha)) seen.set(x.sha, { x, branch: b });
      }
      if (!seen.size) return;
      const r = get(c.name, { url: repo.html_url, private: repo.private });
      r.commits = seen.size;
      r.messages = [...seen.values()].map(({ x, branch }) => ({
        sha: x.sha.slice(0, 7), msg: x.commit.message.split('\n')[0], url: x.html_url, at: new Date(x.commit.author.date),
        branch: branch === repo.default_branch ? null : branch,
      })).sort((a, b) => a.at - b.at);
    } catch {
      if (c.fallback) { const r = get(c.name, c.meta); r.commits = c.fallback; r.noAccess = true; }
    }
  }));
  const list = Object.values(repos).filter((r) => r.commits || r.prs || r.issues || r.reviews || r.created)
    .sort((a, b) => (b.commits + b.prs + b.issues + b.reviews) - (a.commits + a.prs + a.issues + a.reviews));
  return { list, restricted };
}

let syncing = false;
async function sync({ quiet = false } = {}) {
  const { username } = state.settings;
  const token = activeToken();
  if (!username) { if (!quiet) toast('설정에서 GitHub 아이디를 먼저 입력하세요'); return; }
  if (syncing) return;
  syncing = true; $('#syncBtn').disabled = true; $('#syncBtn').textContent = '⟳ 동기화 중…';
  try {
    const [cal, ev] = await Promise.all([
      // 토큰이 거절되면 이벤트(최근 90일)만으로 계속한다
      token ? fetchCalendar(username).catch((e) => { if (tokenRejected) return null; throw e; }) : Promise.resolve(null),
      fetchEventDays(username).catch(() => ({ days: {}, repoDays: {} })),
    ]);
    // 토큰이 거절돼 캘린더를 못 받았으면 전에 받아 둔 1년치 잔디는 그대로 둔다
    const keepOld = !cal && token && tokenRejected;
    const days = { ...(cal || (keepOld ? state.contrib.days : {})) };
    // 캘린더는 UTC 기준일 수 있으므로 최근 날짜는 로컬 시간 기준 이벤트 수와 비교해 큰 값 사용
    for (const [k, v] of Object.entries(ev.days)) days[k] = Math.max(days[k] || 0, v);
    state.contrib = { fetchedAt: new Date().toISOString(), days, repoDays: ev.repoDays, source: cal || keepOld ? 'graphql' : 'events' };
    for (const k of Object.keys(dayCache)) delete dayCache[k];

    // 연동된 프로젝트의 마지막 push 시각 갱신
    if (state.projects.some((p) => p.repo)) {
      const repos = await fetchRepos().catch(() => []);
      const byName = Object.fromEntries(repos.map((r) => [r.full_name.toLowerCase(), r]));
      for (const p of state.projects) {
        const r = p.repo && byName[p.repo.toLowerCase()];
        if (r) p.pushedAt = r.pushed_at;
      }
    }
    save(); render();
    if (!quiet) toast('동기화 완료');
  } catch (e) {
    toast(`동기화 실패 — ${e.message}`);
  } finally {
    syncing = false; $('#syncBtn').disabled = false; $('#syncBtn').textContent = '⟳ 동기화';
  }
}

// ───────────────────────── stats ─────────────────────────
const count = (date) => state.contrib.days[date] || 0;
function stats() {
  const today = new Date();
  const committedToday = count(ymd(today)) > 0;
  let cur = 0;
  for (let d = committedToday ? today : addDays(today, -1); count(ymd(d)) > 0; d = addDays(d, -1)) cur++;
  let longest = 0, run = 0, total = 0, active = 0;
  for (let i = 364; i >= 0; i--) {
    const c = count(ymd(addDays(today, -i)));
    total += c;
    if (c > 0) { active++; run++; longest = Math.max(longest, run); } else run = 0;
  }
  const weekStart = addDays(today, -today.getDay());
  let weekDays = 0;
  for (let i = 0; i <= today.getDay(); i++) if (count(ymd(addDays(weekStart, i))) > 0) weekDays++;
  return { committedToday, cur, longest, total, active, weekDays };
}

// ───────────────────────── manager ─────────────────────────
// 오늘의 미션 후보 순서: ① 오늘 요일에 배정된 프로젝트의 할 일 → ② 오래 방치된 프로젝트 → ③ 나머지 → ④ 작은 커밋 아이디어
function missionCandidates() {
  const dow = new Date().getDay();
  const open = (p) => p.tasks.filter((t) => !t.done).map((t) => ({ project: p, task: t }));
  const staleness = (p) => daysSince(p.pushedAt) ?? daysSince(p.lastWorkedAt) ?? 999;
  const scheduled = state.projects.filter((p) => p.days.includes(dow));
  const others = state.projects.filter((p) => !p.days.includes(dow)).sort((a, b) => staleness(b) - staleness(a));
  const list = [...scheduled.flatMap(open), ...others.flatMap(open)];
  const ideaTarget = scheduled[0] || others[0] || null;
  for (const idea of IDEAS) list.push({ project: ideaTarget, idea });
  return list;
}
function currentMission() {
  if (state.mission.date !== todayStr()) state.mission = { date: todayStr(), skip: 0 };
  const c = missionCandidates();
  return c[state.mission.skip % c.length];
}
function neglected() {
  return state.projects
    .map((p) => ({ p, d: daysSince(p.pushedAt) ?? daysSince(p.lastWorkedAt) }))
    .filter((x) => x.d !== null && x.d >= 7)
    .sort((a, b) => b.d - a.d);
}
function timeLeft() {
  const now = new Date();
  const ms = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1) - now;
  return { h: Math.floor(ms / 3600000), m: Math.floor((ms % 3600000) / 60000), ms };
}

// 리마인더: 페이지가 열려 있는 동안 1분마다 확인
function checkReminder() {
  const s = stats();
  const now = new Date();
  const hhmm = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
  const due = !s.committedToday && hhmm >= state.settings.reminderTime && state.settings.username;
  const banner = $('#banner');
  if (due) {
    const m = currentMission();
    const { h, m: mm } = timeLeft();
    banner.innerHTML = `<b>🌱 아직 오늘 커밋이 없어요</b><span class="grow">자정까지 ${h}시간 ${mm}분 · 추천: ${esc(m.task?.title || m.idea)}</span><button class="btn sm" data-act="sync">확인했어요(동기화)</button>`;
    banner.classList.remove('hidden');
    if (state.settings.notify && state.notifiedOn !== todayStr() && 'Notification' in window && Notification.permission === 'granted') {
      new Notification('Grass Manager — 오늘 잔디가 비어 있어요', {
        body: `${s.cur > 0 ? `${s.cur}일 연속 기록이 끊기기 직전! ` : ''}추천: ${m.task?.title || m.idea}`,
      });
      state.notifiedOn = todayStr(); save();
    }
  } else banner.classList.add('hidden');
}

// ───────────────────────── views ─────────────────────────
function render() {
  $$('.tabs button').forEach((b) => b.classList.toggle('active', b.dataset.tab === state.tab));
  const v = $('#view');
  const prevScroll = $('.grass-wrap')?.scrollLeft;
  v.innerHTML = { dashboard: viewDashboard, projects: viewProjects, planner: viewPlanner, settings: viewSettings }[state.tab]();
  // 좁은 화면에서는 최근 주가 보이도록 오른쪽 끝에서 시작 (다시 그릴 때는 위치 유지)
  const wrap = $('.grass-wrap');
  if (wrap) wrap.scrollLeft = prevScroll ?? wrap.scrollWidth;
  checkReminder();
}

function viewDashboard() {
  if (!state.settings.username) {
    return `<section class="card empty">
      <h2 style="justify-content:center">🌱 Grass Manager에 오신 걸 환영해요</h2>
      <p>GitHub 아이디를 연결하면 잔디를 불러오고, 매일 커밋할 거리를 챙겨드려요.</p>
      <button class="btn primary" data-act="tab" data-tab="settings">GitHub 연결하기</button></section>`;
  }
  const s = stats();
  const m = currentMission();
  const { h, m: mm } = timeLeft();
  const level = s.committedToday ? '' : h < 3 ? 'danger' : h < 8 ? 'risk' : '';
  const status = s.committedToday
    ? `✅ 오늘 잔디 심기 완료 (${count(todayStr())}개)`
    : h < 3 ? '🔥 스트릭 위험! 곧 하루가 끝나요' : '🌱 오늘의 커밋이 아직 없어요';
  const headline = s.committedToday
    ? (s.cur > 1 ? `${s.cur}일 연속 기록 중이에요. 내일도 이어가요!` : '좋아요! 내일도 이어가 볼까요?')
    : (s.cur > 0 ? `${s.cur}일 연속 기록을 지켜주세요` : '오늘 첫 칸을 채워볼까요?');
  const p = m.project;
  const taskHtml = `<div class="task">
      ${p ? `<span class="chip"><span class="sw" style="background:${p.color}"></span>${esc(p.name)}</span>` : ''}
      <span class="what grow">${esc(m.task ? m.task.title : m.idea)}${m.task ? ` <span class="size">${SIZE_LABEL[m.task.size] || ''}</span>` : ' <span class="size">작은 커밋</span>'}</span>
      ${m.task ? `<button class="btn primary sm" data-act="mission-done" data-pid="${p.id}" data-tid="${m.task.id}">완료</button>` : ''}
      <button class="btn sm" data-act="mission-skip">다른 제안</button>
    </div>`;

  const ng = neglected();
  return `
  <section class="card mission ${level}">
    <div>
      <div class="status">${status}</div>
      <h1>${headline}</h1>
      <div class="muted">${s.committedToday ? '여유가 있다면 하나 더 해볼 만한 일:' : '오늘의 추천 미션'}</div>
      ${taskHtml}
    </div>
    <div class="countdown"><div class="muted small">오늘 남은 시간</div><div class="num">${h}:${String(mm).padStart(2, '0')}</div>
      <div class="muted small">${state.contrib.fetchedAt ? `동기화 ${new Date(state.contrib.fetchedAt).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit' })}` : '아직 동기화 안 됨'}</div></div>
  </section>

  <section class="stats">
    <div class="card stat"><div class="label">현재 스트릭</div><div class="value">${s.cur}<small>일</small></div></div>
    <div class="card stat"><div class="label">최장 스트릭 (1년)</div><div class="value">${s.longest}<small>일</small></div></div>
    <div class="card stat"><div class="label">최근 1년 기여</div><div class="value">${s.total}<small>회 · ${s.active}일</small></div></div>
    <div class="card stat"><div class="label">이번 주 활동일</div><div class="value">${s.weekDays}<small>/ ${new Date().getDay() + 1}일</small></div></div>
  </section>

  <section class="card">
    <h2>잔디 <span class="sub">${state.contrib.source === 'events' ? '토큰 없이 최근 90일 공개 활동만 표시 중 — 설정에서 토큰을 넣으면 1년치 공식 잔디' : `@${esc(state.settings.username)}`}</span></h2>
    ${grassHtml()}
    ${dayDetailHtml()}
  </section>

  <div class="grid2">
    <section class="card"><h2>이번 주 <span class="sub"><a href="#" data-act="tab" data-tab="planner">계획 편집</a></span></h2>${weekHtml()}</section>
    <section class="card"><h2>매니저 알림</h2>${managerNotes(s, ng)}</section>
  </div>`;
}

function grassHtml() {
  const today = new Date();
  const start = addDays(today, -364 - today.getDay()); // 53주, 일요일 시작
  const cells = [];
  let lastMonth = -1;
  for (let w = 0; w < 53; w++) {
    const first = addDays(start, w * 7);
    const label = first.getMonth() !== lastMonth && first.getDate() <= 7 ? `${first.getMonth() + 1}월` : '';
    if (label) lastMonth = first.getMonth();
    cells.push(`<span class="m" style="grid-column:${w + 1}">${label}</span>`);
    for (let d = 0; d < 7; d++) {
      const day = addDays(first, d);
      const k = ymd(day);
      const c = count(k);
      const lv = c === 0 ? 0 : c < 3 ? 1 : c < 6 ? 2 : c < 10 ? 3 : 4;
      const future = day > today;
      const cls = ['d', lv && `l${lv}`, k === todayStr() && 'today', future && 'future', k === selectedDay && 'sel'].filter(Boolean).join(' ');
      cells.push(`<span class="${cls}" style="grid-column:${w + 1};grid-row:${d + 2}" title="${k} · ${c}개"${future ? '' : ` data-act="day" data-date="${k}"`}></span>`);
    }
  }
  return `<div class="grass-wrap"><div class="grass">${cells.join('')}</div></div>
    <div class="legend">적음 <i style="background:var(--g0)"></i><i style="background:var(--g1)"></i><i style="background:var(--g2)"></i><i style="background:var(--g3)"></i><i style="background:var(--g4)"></i> 많음</div>`;
}

let selectedDay = null;
function dayDetailHtml() {
  if (!selectedDay) return '<p class="muted small" style="margin:8px 0 0">칸을 누르면 그날 어떤 저장소에 커밋했는지 볼 수 있어요.</p>';
  const d = new Date(`${selectedDay}T00:00:00`);
  const title = `${d.getFullYear()}년 ${d.getMonth() + 1}월 ${d.getDate()}일 (${DAY_NAMES[d.getDay()]})`;
  const res = dayCache[selectedDay];
  let body;
  if (!res) body = '<p class="muted">불러오는 중…</p>';
  else if (res.error) body = `<p class="muted">불러오지 못했어요 — ${esc(res.error)}</p>`;
  else {
    const projOf = (name) => state.projects.find((p) => p.repo && p.repo.toLowerCase() === name.toLowerCase());
    body = res.list.map((r) => {
      const p = projOf(r.name);
      const parts = [r.commits && `커밋 ${r.commits}`, r.prs && `PR ${r.prs}`, r.issues && `이슈 ${r.issues}`, r.reviews && `리뷰 ${r.reviews}`, r.created && '저장소 생성'].filter(Boolean);
      return `<div class="repo-day">
        <div class="row">
          ${p ? `<span class="sw" style="background:${p.color}" title="${esc(p.name)}"></span>` : ''}
          <a href="${esc(r.url)}" target="_blank" rel="noopener"><b>${esc(r.name)}</b></a>${r.private ? ' <span class="size">private</span>' : ''}
          <span class="grow"></span><span class="muted small">${parts.join(' · ')}</span>
        </div>
        ${r.messages ? `<ul class="commits">${r.messages.map((m) => `<li><span class="muted small">${String(m.at.getHours()).padStart(2, '0')}:${String(m.at.getMinutes()).padStart(2, '0')}</span>
          <span class="grow">${esc(m.msg)}${m.branch ? ` <span class="size" title="기본 브랜치가 아닌 커밋은 GitHub 공식 잔디에 집계되지 않아요 (병합되면 집계)">${esc(m.branch)}</span>` : ''}</span>
          <a class="small" href="${esc(m.url)}" target="_blank" rel="noopener"><code>${m.sha}</code></a></li>`).join('')}</ul>
          ${r.messages.some((m) => m.branch) ? '<p class="muted small" style="margin:4px 0 0">ℹ️ 브랜치 표시가 붙은 커밋은 기본 브랜치에 병합되기 전까지 GitHub 공식 잔디에 집계되지 않아요.</p>' : ''}` : ''}
        ${r.noAccess ? '<p class="muted small" style="margin:4px 0 0">커밋 메시지를 보려면 토큰에 이 저장소의 Contents 읽기 권한이 필요해요.</p>' : ''}
      </div>`;
    }).join('');
    if (res.restricted) body += `<p class="muted small">🔒 비공개 기여 ${res.restricted}개 — 토큰으로 볼 수 없는 저장소예요.</p>`;
    if (!body) {
      const old = !activeToken() && daysSince(d) > 90;
      body = `<p class="muted">${old ? '토큰 없이는 최근 90일의 공개 활동만 볼 수 있어요. 설정에서 토큰을 넣어 주세요.' : count(selectedDay) ? '저장소 정보를 찾지 못했어요. 비공개 저장소라면 설정에서 토큰을 넣어 주세요.' : '이날은 기여가 없어요.'}</p>`;
    }
  }
  return `<div class="day-detail">
    <div class="row"><b>${title}</b><span class="muted small">잔디 ${count(selectedDay)}개</span><span class="grow"></span>
      <button class="btn sm ghost" data-act="day-close" aria-label="닫기">✕</button></div>
    ${body}
  </div>`;
}
async function selectDay(date) {
  selectedDay = selectedDay === date ? null : date;
  render();
  if (!selectedDay || dayCache[date]) return;
  try { dayCache[date] = await fetchDayDetail(date); } catch (e) {
    // 토큰이 방금 거절됐으면 토큰 없는 방식으로 다시 불러온다
    if (tokenRejected) { try { dayCache[date] = await fetchDayDetail(date); } catch (e2) { dayCache[date] = { error: e2.message }; } }
    else dayCache[date] = { error: e.message };
  }
  if (selectedDay === date) render();
}

function weekHtml() {
  const today = new Date();
  const start = addDays(today, -today.getDay());
  let html = '<div class="week">';
  for (let i = 0; i < 7; i++) {
    const d = addDays(start, i);
    const k = ymd(d);
    const ps = state.projects.filter((p) => p.days.includes(i));
    html += `<div class="wday ${k === todayStr() ? 'today' : ''}">
      <div class="head"><span>${DAY_NAMES[i]} ${d.getDate()}</span><span class="dot ${count(k) > 0 ? 'on' : ''}" title="${count(k)}개"></span></div>
      ${ps.map((p) => `<span class="chip" title="${esc(p.name)}"><span class="sw" style="background:${p.color}"></span>${esc(p.name)}</span>`).join('') || '<span class="muted small">—</span>'}
    </div>`;
  }
  return html + '</div>';
}

function managerNotes(s, ng) {
  const notes = [];
  const dow = new Date().getDay();
  if (!state.projects.length) notes.push(['📁', '등록된 프로젝트가 없어요. 프로젝트를 추가하거나 GitHub에서 가져오세요.', 'projects']);
  else if (!state.projects.some((p) => p.days.includes(dow))) notes.push(['🗓️', `오늘(${DAY_NAMES[dow]})에 배정된 프로젝트가 없어요. 주간 계획을 세워 두면 매일 고민이 줄어요.`, 'planner']);
  for (const { p, d } of ng.slice(0, 3)) notes.push(['⏳', `<b>${esc(p.name)}</b> — ${d}일째 커밋이 없어요.`, 'projects']);
  const empty = state.projects.filter((p) => !p.tasks.some((t) => !t.done));
  if (empty.length) notes.push(['📝', `할 일이 비어 있는 프로젝트: ${empty.map((p) => esc(p.name)).join(', ')}. 다음 기능을 미리 적어 두세요.`, 'projects']);
  if (tokenRejected) notes.push(['🔑', '<b>토큰이 만료됐거나 취소됐어요.</b> GitHub에서 새 토큰을 만들어 설정에 넣어 주세요. 그 전까지는 최근 90일 공개 활동만 보여요.', 'settings']);
  else if (!state.settings.token) notes.push(['🔑', '토큰을 등록하면 GitHub와 똑같은 1년치 잔디를 볼 수 있어요.', 'settings']);
  if (!state.settings.notify) notes.push(['🔔', `리마인더(${state.settings.reminderTime})를 켜면 커밋이 없는 날 알려드려요.`, 'settings']);
  if (!notes.length) return `<div class="muted">모든 게 순조로워요. 이번 주 ${s.weekDays}일 활동 중 🌿</div>`;
  return notes.map(([i, t, tab]) => `<div class="alert"><span>${i}</span><span class="grow">${t}</span><button class="btn sm" data-act="tab" data-tab="${tab}">이동</button></div>`).join('');
}

function viewProjects() {
  const cards = state.projects.map((p) => {
    const age = daysSince(p.pushedAt);
    const open = p.tasks.filter((t) => !t.done).length;
    return `<article class="card project" style="--pc:${p.color}">
      <div class="row"><div class="grow">
        <div class="title">${esc(p.name)}</div>
        <div class="meta">${p.repo ? `<a href="https://github.com/${esc(p.repo)}" target="_blank" rel="noopener">${esc(p.repo)}</a>` : '수동 프로젝트'}
          ${age !== null ? ` · 마지막 push ${age === 0 ? '오늘' : `${age}일 전`}` : ''} · 남은 할 일 ${open}</div>
      </div>
      <button class="btn sm" data-act="edit-project" data-pid="${p.id}">편집</button></div>
      ${p.desc ? `<div class="muted">${esc(p.desc)}</div>` : ''}
      <div><div class="muted small" style="margin-bottom:4px">작업 요일</div><div class="days">
        ${DAY_NAMES.map((n, i) => `<button class="${p.days.includes(i) ? 'on' : ''}" data-act="toggle-day" data-pid="${p.id}" data-day="${i}">${n}</button>`).join('')}
      </div></div>
      <div>
        <div class="muted small" style="margin-bottom:4px">추가할 기능 / 할 일</div>
        <ul class="tasks">
          ${[...p.tasks].sort((a, b) => a.done - b.done).map((t) => `<li class="${t.done ? 'done' : ''}">
            <input type="checkbox" ${t.done ? 'checked' : ''} data-act="toggle-task" data-pid="${p.id}" data-tid="${t.id}">
            <span class="t">${esc(t.title)}</span><span class="size">${SIZE_LABEL[t.size]}</span>
            <button class="x" data-act="del-task" data-pid="${p.id}" data-tid="${t.id}" title="삭제">✕</button></li>`).join('') || '<li class="muted small">아직 없어요</li>'}
        </ul>
        <form class="row" data-form="add-task" data-pid="${p.id}" style="margin-top:8px">
          <input type="text" name="title" placeholder="예) 로그인 페이지 만들기" class="grow" required>
          <select name="size" style="width:auto">${Object.entries(SIZE_LABEL).map(([k, v]) => `<option value="${k}">${v}</option>`).join('')}</select>
          <button class="btn">추가</button>
        </form>
      </div>
    </article>`;
  }).join('');
  return `<div class="row"><h2 class="grow" style="margin:0;font-size:18px">내 프로젝트</h2>
      <button class="btn" data-act="import">GitHub에서 가져오기</button>
      <button class="btn primary" data-act="edit-project">+ 새 프로젝트</button></div>
    ${state.projects.length ? `<div class="projects">${cards}</div>` : '<div class="card empty">아직 프로젝트가 없어요. 직접 추가하거나 GitHub 저장소를 가져오세요.</div>'}`;
}

function viewPlanner() {
  const dow = new Date().getDay();
  if (!state.projects.length) return '<div class="card empty">프로젝트를 먼저 추가하세요. <button class="btn" data-act="tab" data-tab="projects">프로젝트로</button></div>';
  const uncovered = DAY_NAMES.filter((_, i) => !state.projects.some((p) => p.days.includes(i)));
  return `<section class="card">
    <h2>주간 작업 계획 <span class="sub">요일마다 최소 한 프로젝트를 배정하면 "오늘 뭐 하지?"가 사라져요</span></h2>
    <div style="overflow-x:auto"><table class="plan-table">
      <thead><tr><th>프로젝트</th>${DAY_NAMES.map((n, i) => `<th class="${i === dow ? 'today' : ''}">${n}</th>`).join('')}<th>남은 할 일</th></tr></thead>
      <tbody>${state.projects.map((p) => `<tr><td><span class="chip"><span class="sw" style="background:${p.color}"></span>${esc(p.name)}</span></td>
        ${DAY_NAMES.map((_, i) => `<td class="${i === dow ? 'today' : ''}"><input type="checkbox" ${p.days.includes(i) ? 'checked' : ''} data-act="toggle-day" data-pid="${p.id}" data-day="${i}" aria-label="${esc(p.name)} ${DAY_NAMES[i]}"></td>`).join('')}
        <td>${p.tasks.filter((t) => !t.done).length}</td></tr>`).join('')}</tbody>
    </table></div>
    ${uncovered.length ? `<div class="alert" style="margin-top:12px">⚠️ 비어 있는 요일: <b>${uncovered.join(', ')}</b> — 이날은 '작은 커밋' 아이디어가 추천돼요.</div>` : '<p class="muted">모든 요일에 프로젝트가 배정됐어요 👍</p>'}
    <div class="row" style="margin-top:8px"><button class="btn" data-act="auto-plan">빈 요일 자동 배정</button><span class="muted small">할 일이 많은 프로젝트부터 빈 요일에 채워 넣어요</span></div>
  </section>
  <section class="card"><h2>이번 주 한눈에</h2>${weekHtml()}</section>`;
}

function viewSettings() {
  const s = state.settings;
  const perm = 'Notification' in window ? Notification.permission : 'unsupported';
  return `<section class="card">
    <h2>GitHub 연결</h2>
    <form data-form="settings" style="display:grid;gap:12px">
      <label class="field">GitHub 아이디<input type="text" name="username" value="${esc(s.username)}" placeholder="octocat" autocomplete="off"></label>
      <label class="field">Personal Access Token <span class="muted">(선택) GitHub와 동일한 1년치 잔디·비공개 저장소용. Settings → Developer settings → Fine-grained token, 권한은 읽기 전용이면 충분해요. 이 브라우저에만 저장됩니다.</span>
        <input type="password" name="token" value="${esc(s.token)}" placeholder="github_pat_..." autocomplete="off"></label>
      <label class="field">리마인더 시각 <span class="muted">이 시각까지 커밋이 없으면 배너(와 알림)로 알려드려요</span>
        <input type="time" name="reminderTime" value="${esc(s.reminderTime)}" style="width:auto"></label>
      <div class="row"><button class="btn primary">저장 후 동기화</button></div>
    </form>
  </section>
  <section class="card">
    <h2>브라우저 알림</h2>
    <p class="muted">이 페이지(탭)가 열려 있는 동안 리마인더 시각에 데스크톱 알림을 보내요. 현재 권한: <b>${perm}</b></p>
    <div class="row">
      <button class="btn ${s.notify ? '' : 'primary'}" data-act="notify-toggle">${s.notify ? '알림 끄기' : '알림 켜기'}</button>
      <button class="btn" data-act="notify-test">테스트 알림</button>
    </div>
  </section>
  ${remoteCard()}
  <section class="card">
    <h2>데이터</h2>
    <p class="muted">프로젝트·할 일·설정은 이 브라우저의 localStorage에 저장돼요. 백업해 두세요.</p>
    <div class="row">
      <button class="btn" data-act="export">JSON 내보내기</button>
      <label class="btn">JSON 가져오기<input type="file" accept="application/json" data-act="import-json" hidden></label>
      <button class="btn danger" data-act="reset">전체 초기화</button>
    </div>
  </section>`;
}

// ───────────────────────── 탭이 닫혀도 알림 (GitHub Actions) ─────────────────────────
// 로컬 HH:MM → UTC cron (GitHub Actions의 schedule은 UTC 기준)
function toCron(hhmm) {
  const [h, m] = hhmm.split(':').map(Number);
  const d = new Date(); d.setHours(h, m, 0, 0);
  return `${d.getUTCMinutes()} ${d.getUTCHours()} * * *`;
}
function workflowYaml() {
  const s = state.settings;
  const tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const times = [...new Set([s.reminderTime, s.lastCall].filter(Boolean))].sort();
  return `name: Grass Reminder
on:
  schedule:
${times.map((t) => `    - cron: '${toCron(t)}'   # ${t} (${tz})`).join('\n')}
  workflow_dispatch:   # Actions 탭에서 수동 실행(테스트)용
permissions:
  contents: read
jobs:
  remind:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: node .github/scripts/grass-check.mjs
        env:
          GH_LOGIN: \${{ github.repository_owner }}
          GH_TOKEN: \${{ secrets.GRASS_TOKEN || github.token }}
          GRASS_TZ: ${tz}
          NTFY_TOPIC: \${{ secrets.NTFY_TOPIC }}
          DISCORD_WEBHOOK: \${{ secrets.DISCORD_WEBHOOK }}
          FAIL_ON_MISS: '${s.failOnMiss ? 'true' : 'false'}'
          PLAN_FILE: grass-plan.json
`;
}
// Actions가 읽을 계획 파일: 할 일이 남은 것만 간추린다
function planJson() {
  return JSON.stringify({
    exportedAt: new Date().toISOString(),
    projects: state.projects.map((p) => ({ name: p.name, repo: p.repo, days: p.days, tasks: p.tasks.filter((t) => !t.done).map((t) => ({ title: t.title, size: t.size })) })),
  }, null, 2);
}
function download(name, text, type = 'text/plain') {
  const a = Object.assign(document.createElement('a'), { href: URL.createObjectURL(new Blob([text], { type })), download: name });
  a.click(); URL.revokeObjectURL(a.href);
}
async function copy(text) {
  try { await navigator.clipboard.writeText(text); toast('복사했어요'); } catch { toast('복사 실패 — 직접 선택해서 복사하세요'); }
}

function remoteCard() {
  const s = state.settings;
  if (!s.ntfyTopic) { s.ntfyTopic = `grass-${(s.username || 'me').toLowerCase()}-${uid()}`; save(); }
  return `<section class="card">
    <h2>탭이 닫혀도 알림 받기 <span class="sub">GitHub Actions가 매일 대신 확인해요 · PC가 꺼져 있어도 동작</span></h2>
    <form data-form="remote" style="display:grid;gap:12px">
      <div class="grid2">
        <label class="field">1차 알림 시각 <span class="muted">위 '리마인더 시각'과 같아요</span><input type="time" value="${esc(s.reminderTime)}" disabled style="width:auto"></label>
        <label class="field">마지막 알림 시각<input type="time" name="lastCall" value="${esc(s.lastCall)}" style="width:auto"></label>
      </div>
      <label class="field">ntfy 토픽 <span class="muted">폰/PC에 <a href="https://ntfy.sh" target="_blank" rel="noopener">ntfy</a> 앱을 설치하고 이 토픽을 구독하세요. 토픽 이름을 아는 사람은 누구나 볼 수 있으니 추측하기 어렵게 두세요.</span>
        <div class="row"><input type="text" name="ntfyTopic" value="${esc(s.ntfyTopic)}" class="grow"><button type="button" class="btn" data-act="ntfy-test">테스트 전송</button></div></label>
      <label class="row"><input type="checkbox" name="failOnMiss" ${s.failOnMiss ? 'checked' : ''}> 커밋이 없으면 워크플로를 실패 처리해서 GitHub 메일 알림도 받기</label>
      <div class="row"><button class="btn primary">저장</button></div>
    </form>
    <ol class="steps">
      <li>알림용 저장소를 하나 만들어요 (예: <code>grass-reminder</code>, 비공개 가능).</li>
      <li>아래 두 파일을 그 저장소에 추가해요.
        <div class="row" style="margin-top:6px">
          <code class="grow">.github/workflows/grass-reminder.yml</code>
          <button class="btn sm" data-act="copy-yaml">복사</button><button class="btn sm" data-act="dl-yaml">다운로드</button>
        </div>
        <div class="row" style="margin-top:6px">
          <code class="grow">.github/scripts/grass-check.mjs</code>
          <button class="btn sm" data-act="copy-script">복사</button><button class="btn sm" data-act="dl-script">다운로드</button>
        </div>
        <div class="row" style="margin-top:6px">
          <code class="grow">grass-plan.json</code> <span class="muted small">(선택) 알림에 오늘의 할 일을 넣어줘요. 계획을 바꾸면 다시 올려 주세요.</span>
          <button class="btn sm" data-act="dl-plan">다운로드</button>
        </div>
      </li>
      <li>저장소 <b>Settings → Secrets and variables → Actions</b>에 Secret을 추가해요.
        <ul>
          <li><code>NTFY_TOPIC</code> = <code>${esc(s.ntfyTopic)}</code></li>
          <li><code>GRASS_TOKEN</code> (권장) — 읽기 전용 PAT. 비공개 저장소 기여까지 정확히 셀 수 있어요.</li>
          <li><code>DISCORD_WEBHOOK</code> (선택) — 디스코드 채널로도 받고 싶을 때</li>
        </ul></li>
      <li><b>Actions → Grass Reminder → Run workflow</b>로 한 번 실행해 알림이 오는지 확인해요.</li>
    </ol>
    <p class="muted small">참고: GitHub 예약 실행은 몇 분~수십 분 늦을 수 있어요. 공개 저장소는 60일간 커밋이 없으면 예약 실행이 꺼지니, 가끔 grass-plan.json을 갱신해 주세요.</p>
    <details><summary class="muted small">워크플로 미리보기</summary><pre class="code">${esc(workflowYaml())}</pre></details>
  </section>`;
}

let scriptCache = null;
async function checkScript() {
  if (scriptCache) return scriptCache;
  const r = await fetch('reminder/grass-check.mjs');
  if (!r.ok) throw new Error('reminder/grass-check.mjs 를 읽지 못했어요');
  return (scriptCache = await r.text());
}

// ───────────────────────── modals ─────────────────────────
function openModal(html) { const m = $('#modal'); $('.modal-box', m).innerHTML = html; m.classList.remove('hidden'); }
function closeModal() { $('#modal').classList.add('hidden'); }

function projectModal(p) {
  const isNew = !p;
  p = p || { name: '', repo: '', desc: '', color: COLORS[state.projects.length % COLORS.length] };
  openModal(`<h3>${isNew ? '새 프로젝트' : '프로젝트 편집'}</h3>
    <form data-form="project" data-pid="${p.id || ''}" style="display:grid;gap:12px">
      <label class="field">이름<input type="text" name="name" value="${esc(p.name)}" required></label>
      <label class="field">GitHub 저장소 <span class="muted">(선택) owner/repo 형식 — 마지막 push 날짜를 추적해요</span><input type="text" name="repo" value="${esc(p.repo)}" placeholder="${esc(state.settings.username || 'owner')}/my-project"></label>
      <label class="field">설명 / 목표<textarea name="desc" rows="2">${esc(p.desc)}</textarea></label>
      <label class="field">색상<select name="color">${COLORS.map((c) => `<option value="${c}" ${c === p.color ? 'selected' : ''} style="color:${c}">■ ${c}</option>`).join('')}</select></label>
      <div class="row">${isNew ? '' : '<button type="button" class="btn danger" data-act="del-project">삭제</button>'}<span class="grow"></span>
        <button type="button" class="btn" data-act="close">취소</button><button class="btn primary">저장</button></div>
    </form>`);
}

async function importModal() {
  if (!state.settings.username) { toast('설정에서 GitHub 아이디를 먼저 입력하세요'); return; }
  openModal('<h3>GitHub 저장소 가져오기</h3><p class="muted">불러오는 중…</p>');
  try {
    const repos = (await fetchRepos()).filter((r) => !r.fork && !r.archived);
    const have = new Set(state.projects.map((p) => (p.repo || '').toLowerCase()));
    openModal(`<h3>GitHub 저장소 가져오기</h3><p class="muted small">최근 push 순 · 포크/보관된 저장소 제외</p>
      <form data-form="import" style="display:grid;gap:12px">
        <div class="repo-list">${repos.map((r) => `<label><input type="checkbox" name="repo" value="${esc(r.full_name)}" ${have.has(r.full_name.toLowerCase()) ? 'disabled checked' : ''}>
          <span class="grow"><b>${esc(r.name)}</b>${r.private ? ' <span class="size">private</span>' : ''}<br><span class="muted small">${r.description ? `${esc(r.description)} · ` : ''}${daysSince(r.pushed_at)}일 전 push</span></span></label>`).join('') || '<p class="muted">저장소가 없어요</p>'}</div>
        <div class="row"><span class="grow"></span><button type="button" class="btn" data-act="close">취소</button><button class="btn primary">가져오기</button></div>
      </form>`);
    importModal.repos = repos;
  } catch (e) {
    openModal(`<h3>불러오기 실패</h3><p>${esc(e.message)}</p><div class="row"><button class="btn" data-act="close">닫기</button></div>`);
  }
}

// ───────────────────────── events ─────────────────────────
const findP = (id) => state.projects.find((p) => p.id === id);
function toast(msg) {
  const t = $('#toast'); t.textContent = msg; t.classList.remove('hidden');
  clearTimeout(toast.t); toast.t = setTimeout(() => t.classList.add('hidden'), 2800);
}
function commit() { save(); render(); }

document.addEventListener('click', async (e) => {
  const el = e.target.closest('[data-act]');
  if (!el || el.matches('input[type=file]')) return;
  const { act, pid, tid } = el.dataset;
  if (el.tagName === 'A') e.preventDefault();
  switch (act) {
    case 'tab': state.tab = el.dataset.tab; commit(); break;
    case 'sync': sync(); break;
    case 'close': closeModal(); break;
    case 'day': selectDay(el.dataset.date); break;
    case 'day-close': selectedDay = null; render(); break;
    case 'mission-skip': state.mission.skip++; commit(); break;
    case 'mission-done': {
      const p = findP(pid); const t = p?.tasks.find((x) => x.id === tid);
      if (t) { t.done = true; t.doneAt = new Date().toISOString(); p.lastWorkedAt = t.doneAt; }
      commit(); toast('완료! 커밋 & push 했다면 동기화해서 잔디를 확인하세요 🌱'); break;
    }
    case 'toggle-day': {
      const p = findP(pid); const d = +el.dataset.day;
      p.days = p.days.includes(d) ? p.days.filter((x) => x !== d) : [...p.days, d].sort();
      commit(); break;
    }
    case 'toggle-task': {
      const t = findP(pid).tasks.find((x) => x.id === tid);
      t.done = !t.done; t.doneAt = t.done ? new Date().toISOString() : null;
      if (t.done) findP(pid).lastWorkedAt = t.doneAt;
      commit(); break;
    }
    case 'del-task': { const p = findP(pid); p.tasks = p.tasks.filter((x) => x.id !== tid); commit(); break; }
    case 'edit-project': projectModal(pid ? findP(pid) : null); break;
    case 'del-project': {
      const id = $('#modal form').dataset.pid;
      if (confirm('이 프로젝트와 할 일을 삭제할까요?')) { state.projects = state.projects.filter((p) => p.id !== id); closeModal(); commit(); }
      break;
    }
    case 'import': importModal(); break;
    case 'auto-plan': {
      const ranked = [...state.projects].sort((a, b) => b.tasks.filter((t) => !t.done).length - a.tasks.filter((t) => !t.done).length);
      let k = 0;
      for (let d = 0; d < 7; d++) {
        if (state.projects.some((p) => p.days.includes(d))) continue;
        const p = ranked[k++ % ranked.length]; p.days = [...p.days, d].sort();
      }
      commit(); toast('빈 요일을 채웠어요'); break;
    }
    case 'notify-toggle': {
      if (!state.settings.notify) {
        if (!('Notification' in window)) { toast('이 브라우저는 알림을 지원하지 않아요'); break; }
        const perm = await Notification.requestPermission();
        if (perm !== 'granted') { toast('알림 권한이 거부됐어요 — 화면 배너로만 알려드릴게요'); break; }
      }
      state.settings.notify = !state.settings.notify; commit(); break;
    }
    case 'notify-test':
      if ('Notification' in window && Notification.permission === 'granted') new Notification('Grass Manager', { body: '알림이 정상 동작해요 🌱' });
      else toast('먼저 알림을 켜 주세요');
      break;
    case 'copy-yaml': copy(workflowYaml()); break;
    case 'dl-yaml': download('grass-reminder.yml', workflowYaml(), 'text/yaml'); break;
    case 'copy-script': try { copy(await checkScript()); } catch (err) { toast(err.message); } break;
    case 'dl-script': try { download('grass-check.mjs', await checkScript(), 'text/javascript'); } catch (err) { toast(err.message); } break;
    case 'dl-plan': download('grass-plan.json', planJson(), 'application/json'); break;
    case 'ntfy-test': {
      const topic = $('form[data-form=remote] input[name=ntfyTopic]').value.trim();
      if (!topic) { toast('토픽을 입력하세요'); break; }
      try {
        const q = new URLSearchParams({ title: 'Grass Manager 테스트', tags: 'seedling' });
        const r = await fetch(`https://ntfy.sh/${encodeURIComponent(topic)}?${q}`, { method: 'POST', body: '이 알림이 보이면 연결 성공이에요 🌱' });
        toast(r.ok ? '보냈어요 — ntfy 앱에서 확인하세요' : `전송 실패 (${r.status})`);
      } catch (err) { toast(`전송 실패 — ${err.message}`); }
      break;
    }
    case 'export': {
      const blob = new Blob([JSON.stringify({ ...state, settings: { ...state.settings, token: '' } }, null, 2)], { type: 'application/json' });
      const a = Object.assign(document.createElement('a'), { href: URL.createObjectURL(blob), download: `grass-manager-${todayStr()}.json` });
      a.click(); URL.revokeObjectURL(a.href); break;
    }
    case 'reset':
      if (confirm('모든 프로젝트·설정을 지울까요? 되돌릴 수 없어요.')) { state = defaults(); commit(); }
      break;
  }
});

document.addEventListener('change', async (e) => {
  if (e.target.dataset.act !== 'import-json') return;
  try {
    const data = JSON.parse(await e.target.files[0].text());
    if (!Array.isArray(data.projects)) throw new Error('형식이 올바르지 않아요');
    const token = state.settings.token;
    state = { ...defaults(), ...data, settings: { ...defaults().settings, ...data.settings, token } };
    commit(); toast('가져오기 완료');
  } catch (err) { toast(`가져오기 실패 — ${err.message}`); }
});

document.addEventListener('submit', (e) => {
  const f = e.target; const kind = f.dataset.form; if (!kind) return;
  e.preventDefault();
  const fd = new FormData(f);
  if (kind === 'add-task') {
    const title = fd.get('title').trim(); if (!title) return;
    findP(f.dataset.pid).tasks.push({ id: uid(), title, size: fd.get('size'), done: false, createdAt: new Date().toISOString() });
    commit();
    $(`form[data-pid="${f.dataset.pid}"] input[name=title]`)?.focus();
  } else if (kind === 'project') {
    const data = { name: fd.get('name').trim(), repo: fd.get('repo').trim().replace(/^https?:\/\/github\.com\//, '').replace(/\/$/, ''), desc: fd.get('desc').trim(), color: fd.get('color') };
    const p = findP(f.dataset.pid);
    if (p) Object.assign(p, data);
    else state.projects.push({ id: uid(), days: [], tasks: [], pushedAt: null, lastWorkedAt: null, ...data });
    closeModal(); commit();
    if (data.repo) sync({ quiet: true });
  } else if (kind === 'import') {
    const picked = fd.getAll('repo');
    for (const name of picked) {
      const r = importModal.repos.find((x) => x.full_name === name);
      state.projects.push({ id: uid(), name: r.name, repo: r.full_name, desc: r.description || '', color: COLORS[state.projects.length % COLORS.length], days: [], tasks: [], pushedAt: r.pushed_at, lastWorkedAt: null });
    }
    closeModal(); commit(); toast(`${picked.length}개 프로젝트를 가져왔어요 — 작업 요일과 할 일을 정해 주세요`);
  } else if (kind === 'remote') {
    Object.assign(state.settings, { lastCall: fd.get('lastCall') || '', ntfyTopic: fd.get('ntfyTopic').trim(), failOnMiss: fd.get('failOnMiss') === 'on' });
    commit(); toast('저장했어요 — 워크플로 파일을 다시 복사해 올려 주세요');
  } else if (kind === 'settings') {
    const changedUser = fd.get('username').trim() !== state.settings.username;
    Object.assign(state.settings, { username: fd.get('username').trim(), token: fd.get('token').trim(), reminderTime: fd.get('reminderTime') || '21:00' });
    if (changedUser) state.contrib = defaults().contrib;
    tokenRejected = false; // 새 토큰으로 다시 시도
    for (const k of Object.keys(dayCache)) delete dayCache[k];
    state.notifiedOn = null; state.tab = 'dashboard';
    commit(); sync();
  }
});

$('#modal').addEventListener('click', (e) => { if (e.target.id === 'modal') closeModal(); });
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeModal(); });
$('.tabs').addEventListener('click', (e) => { const b = e.target.closest('button'); if (b) { state.tab = b.dataset.tab; commit(); } });
$('#syncBtn').addEventListener('click', () => sync());

// ───────────────────────── boot ─────────────────────────
render();
if (state.settings.username) sync({ quiet: true });
setInterval(() => { if (state.tab === 'dashboard' && !$('#view input:focus')) render(); else checkReminder(); }, 60_000);
setInterval(() => sync({ quiet: true }), 30 * 60_000);
document.addEventListener('visibilitychange', () => {
  // 다른 탭에서 커밋하고 돌아오면 바로 반영 (10분 이상 지났을 때)
  if (!document.hidden && state.settings.username && Date.now() - new Date(state.contrib.fetchedAt || 0) > 10 * 60_000) sync({ quiet: true });
});
