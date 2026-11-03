// Grass Manager — 탭이 닫혀 있어도 동작하는 리마인더 (GitHub Actions에서 실행)
// 오늘(로컬 시간대 기준) 기여가 없으면 ntfy / Discord 로 알림을 보내고, 원하면 job을 실패시켜 GitHub 메일 알림도 받는다.
import { readFile } from 'node:fs/promises';

const env = process.env;
const LOGIN = env.GH_LOGIN;
const TOKEN = env.GH_TOKEN;
const TZ = env.GRASS_TZ || 'Asia/Seoul';
const PLAN_FILE = env.PLAN_FILE || 'grass-plan.json';
const DAY_NAMES = ['일', '월', '화', '수', '목', '금', '토'];
const IDEAS = ['README에 사용법 한 줄 추가하기', '코드 속 TODO 하나 해결하기', '테스트 케이스 하나 추가하기', '오타·주석 정리하기', '다음 할 일을 TODO.md로 정리하기'];

if (!LOGIN || !TOKEN) { console.error('GH_LOGIN / GH_TOKEN 이 필요해요'); process.exit(2); }

// 시간대 계산: Intl로 해당 시간대의 날짜/오프셋을 구한다
function zoned(date = new Date()) {
  const p = Object.fromEntries(new Intl.DateTimeFormat('en-US', {
    timeZone: TZ, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', weekday: 'short',
  }).formatToParts(date).map((x) => [x.type, x.value]));
  const asUTC = Date.UTC(+p.year, +p.month - 1, +p.day, +p.hour, +p.minute, +p.second);
  const offset = asUTC - Math.floor(date.getTime() / 1000) * 1000;
  return {
    ymd: `${p.year}-${p.month}-${p.day}`,
    dow: ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].indexOf(p.weekday),
    midnight: new Date(Date.UTC(+p.year, +p.month - 1, +p.day) - offset),
    hoursLeft: 24 - +p.hour - (+p.minute > 0 ? 1 : 0),
    minutesLeft: (60 - +p.minute) % 60,
  };
}

async function graphql(query, variables) {
  const r = await fetch('https://api.github.com/graphql', {
    method: 'POST',
    headers: { Authorization: `Bearer ${TOKEN}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, variables }),
  });
  const j = await r.json();
  if (!r.ok || j.errors) throw new Error(j.errors?.[0]?.message || j.message || `GraphQL ${r.status}`);
  return j.data;
}

const now = zoned();
const data = await graphql(`query($login:String!, $from:DateTime!, $to:DateTime!){
  user(login:$login){
    today: contributionsCollection(from:$from, to:$to){
      totalCommitContributions totalIssueContributions totalPullRequestContributions
      totalPullRequestReviewContributions totalRepositoryContributions restrictedContributionsCount
    }
    year: contributionsCollection{ contributionCalendar{ weeks{ contributionDays{ date contributionCount } } } }
  }
}`, { login: LOGIN, from: now.midnight.toISOString(), to: new Date().toISOString() });

const t = data.user.today;
let todayCount = t.totalCommitContributions + t.totalIssueContributions + t.totalPullRequestContributions
  + t.totalPullRequestReviewContributions + t.totalRepositoryContributions + t.restrictedContributionsCount;

// 보조 확인: 최근 이벤트 중 오늘(로컬) push가 있으면 반영 (contributionsCollection 반영 지연 대비)
try {
  const r = await fetch(`https://api.github.com/users/${encodeURIComponent(LOGIN)}/events?per_page=100`, { headers: { Authorization: `Bearer ${TOKEN}` } });
  if (r.ok) for (const e of await r.json()) if (e.type === 'PushEvent' && zoned(new Date(e.created_at)).ymd === now.ymd) todayCount++;
} catch {}

// 스트릭: 어제부터 거꾸로 센다
const cal = {};
for (const w of data.user.year.contributionCalendar.weeks) for (const d of w.contributionDays) cal[d.date] = d.contributionCount;
let streak = 0;
for (let d = new Date(now.midnight.getTime() + 12 * 3600e3 - 86400e3); cal[zoned(d).ymd] > 0; d = new Date(d.getTime() - 86400e3)) streak++;

console.log(`${now.ymd} (${TZ}) 오늘 기여: ${todayCount}, 어제까지 스트릭: ${streak}`);
if (todayCount > 0) { console.log('오늘 잔디 완료 ✅ — 알림 없음'); process.exit(0); }

// 추천 미션: 앱에서 내보낸 grass-plan.json 이 있으면 오늘 요일의 할 일을 고른다
let mission = IDEAS[new Date().getDate() % IDEAS.length];
let project = '';
try {
  const plan = JSON.parse(await readFile(PLAN_FILE, 'utf8'));
  const ps = [...plan.projects].sort((a, b) => b.days.includes(now.dow) - a.days.includes(now.dow));
  const pick = ps.find((p) => p.tasks?.length) || ps[0];
  if (pick) { project = pick.name; if (pick.tasks?.length) mission = pick.tasks[0].title; }
} catch {}

const title = streak > 0 ? `🔥 ${streak}일 연속 기록이 끊기기 직전!` : '🌱 오늘 잔디가 아직 비어 있어요';
const body = `자정까지 ${now.hoursLeft}시간 ${now.minutesLeft}분 남았어요.\n오늘의 추천${project ? ` (${project})` : ''}: ${mission}`;
console.log(`${title}\n${body}`);

const sent = [];
if (env.NTFY_TOPIC) {
  const server = env.NTFY_SERVER || 'https://ntfy.sh';
  // 헤더는 ASCII만 허용되므로 한글 제목 등은 쿼리 파라미터로 보낸다
  const q = new URLSearchParams({ title, priority: now.hoursLeft < 3 ? 'high' : 'default', tags: 'seedling', click: `https://github.com/${LOGIN}` });
  const r = await fetch(`${server}/${encodeURIComponent(env.NTFY_TOPIC)}?${q}`, { method: 'POST', body });
  sent.push(`ntfy ${r.status}`);
}
if (env.DISCORD_WEBHOOK) {
  const r = await fetch(env.DISCORD_WEBHOOK, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ content: `**${title}**\n${body}` }),
  });
  sent.push(`discord ${r.status}`);
}
console.log(sent.length ? `알림 전송: ${sent.join(', ')}` : '설정된 알림 채널이 없어요 (NTFY_TOPIC / DISCORD_WEBHOOK)');

// FAIL_ON_MISS=true 이면 job을 실패 처리 → GitHub가 저장소 주인에게 실패 메일을 보낸다
if (env.FAIL_ON_MISS === 'true') { console.error('오늘 커밋 없음 — 메일 알림을 위해 실패 처리'); process.exit(1); }
