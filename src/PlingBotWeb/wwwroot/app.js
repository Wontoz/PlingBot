let leagueMap = {};
let latestMatches = [];
let latestEvents = [];
let latestFixtureMap = {};
let latestHasStarted = true;
let activeTab = 'live';
let selectedMatchNumber = null;
let lineupSubsExpanded = false;

// ── Data fetching ─────────────────────────────────────────────────────────────

async function refresh() {
  try {
    const res = await fetch('/api/coupon');
    if (!res.ok) throw new Error(res.status);
    const data = await res.json();
    renderAll(data);
    document.getElementById('error').style.display = 'none';
  } catch {
    document.getElementById('error').style.display = 'block';
  }
}

// ── Top-level render ──────────────────────────────────────────────────────────

function renderAll(data) {
  const meta    = data.MetaData;
  const matches = data.TipsData   || [];
  const events  = data.Events     || [];
  leagueMap = meta.LeagueMap || {};

  const _titleEl = document.getElementById('game-title');
  const _logoMap = { stryktipset: 'stryktipset', europatipset: 'europatipset', topptipset: 'topptipset' };
  const _logoKey = Object.keys(_logoMap).find(k => (meta.Game || '').toLowerCase().includes(k));
  if (_logoKey) {
    _titleEl.innerHTML = `<img class="game-logo" src="/assets/img/${_logoKey}.png" alt="${meta.Game}"><span class="header-date">${meta.Date}</span>`;
  } else {
    _titleEl.textContent = `${meta.Game} - ${meta.Date}`;
  }
  applyGameClass(meta.Game);

  const fixtureMap  = buildFixtureMap(matches);
  const hasStarted  = !meta.StartTime || new Date(meta.StartTime) <= new Date();

  latestMatches    = matches;
  latestEvents     = events;
  latestFixtureMap = fixtureMap;
  latestHasStarted = hasStarted;

  renderMatchesList();
  const matchesEl = document.getElementById('matches');
  matchesEl.classList.add('matches-compact');
  matchesEl.classList.toggle('matches-few', matches.length <= 8);
  document.getElementById('stats-grid').innerHTML = renderStats(matches, events, meta.Payouts || []);

  const statsPanel = document.querySelector('.stats-panel');
  if (statsPanel) statsPanel.style.display = hasStarted ? '' : 'none';

  renderTabs();
  renderActiveTabContent();

  document.getElementById('poll-time').textContent =
    'Uppdaterad ' + new Date().toLocaleTimeString('sv-SE', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

  const dataTimeEl = document.getElementById('data-time');
  if (!hasStarted && meta.DataLastUpdatedUtc) {
    const t = new Date(meta.DataLastUpdatedUtc).toLocaleTimeString('sv-SE', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    dataTimeEl.textContent = ` · Data: ${t}`;
  } else {
    dataTimeEl.textContent = '';
  }
}

// ── Panel tabs (Live / per-match Statistik / Händelser) ────────────────────────

function filterLiveEvents(events) {
  return events.filter(e =>
    e.Type === 'Goal' ||
    e.Type === 'CancelledGoal' ||
    (e.Type === 'Card' && e.Detail !== 'Yellow Card'));
}

function hasMatchStats(tip) {
  const s = tip?.Statistics;
  if (!s) return false;
  const h = s.Home, a = s.Away;
  return (h && Object.entries(h).some(([k, v]) => k !== 'TeamName' && v != null)) ||
         (a && Object.entries(a).some(([k, v]) => k !== 'TeamName' && v != null));
}

function hasMatchEvents(tip) {
  return !!tip && latestEvents.some(e => e.FixtureId === tip.FixtureId);
}

function hasMatchLineups(tip) {
  if (!tip) return false;
  return (!!tip.HomeLineup && !!tip.AwayLineup) || !!(tip.Injuries?.length);
}

function hasMatchH2H(tip) {
  return !!(tip?.H2H?.length);
}

const MATCH_TAB_CHECKS = {
  'match-stats':   hasMatchStats,
  'match-events':  hasMatchEvents,
  'match-lineup':  hasMatchLineups,
  'match-h2h':     hasMatchH2H,
};

// Picks the tab to land on for a given match: prefers Statistik, falls back to
// Händelser, falls back to Laguppställning, falls back to H2H, falls back to Live.
function pickMatchTab(tip) {
  for (const tab of ['match-stats', 'match-events', 'match-lineup', 'match-h2h'])
    if (MATCH_TAB_CHECKS[tab](tip)) return tab;
  return 'live';
}

function renderTabs() {
  const liveLabel  = latestHasStarted ? 'Live' : 'Inför omgången';
  const liveEvents = filterLiveEvents(latestEvents);
  const badge      = latestHasStarted && liveEvents.length ? `<span class="panel-badge">${liveEvents.length}</span>` : '';

  let html = `<button class="panel-tab ${activeTab === 'live' ? 'active' : ''}" data-tab="live">${liveLabel}${badge}</button>`;

  if (selectedMatchNumber != null) {
    const tip = latestMatches.find(m => m.Number === selectedMatchNumber);

    if (hasMatchStats(tip))
      html += `<button class="panel-tab ${activeTab === 'match-stats' ? 'active' : ''}" data-tab="match-stats"><span class="tab-full">Statistik</span><span class="tab-short">Stats</span></button>`;
    if (hasMatchEvents(tip))
      html += `<button class="panel-tab ${activeTab === 'match-events' ? 'active' : ''}" data-tab="match-events">Händelser</button>`;
    if (hasMatchLineups(tip))
      html += `<button class="panel-tab ${activeTab === 'match-lineup' ? 'active' : ''}" data-tab="match-lineup"><span class="tab-full">Laginfo</span><span class="tab-short">Lag</span></button>`;
    if (hasMatchH2H(tip))
      html += `<button class="panel-tab ${activeTab === 'match-h2h' ? 'active' : ''}" data-tab="match-h2h">H2H</button>`;
    html += `<button class="panel-tab-close" data-tab="close" title="Stäng">✕</button>`;
  }

  document.getElementById('panel-tabs').innerHTML = html;
}

function renderActiveTabContent() {
  const container = document.getElementById('events-list');
  container.classList.toggle('events-list-scroll', activeTab === 'live');

  if (activeTab === 'live') {
    container.innerHTML = latestHasStarted
      ? renderEventsList(filterLiveEvents(latestEvents), latestFixtureMap)
      : renderPreMatch(latestMatches);
    return;
  }

  const tip = latestMatches.find(m => m.Number === selectedMatchNumber);
  if (!tip) {
    activeTab = 'live';
    selectedMatchNumber = null;
    renderTabs();
    renderActiveTabContent();
    return;
  }

  if (activeTab === 'match-events') {
    const matchEvents = latestEvents.filter(e => e.FixtureId === tip.FixtureId);
    container.innerHTML = renderMatchEventsList(matchEvents, latestFixtureMap);
  } else if (activeTab === 'match-stats') {
    container.innerHTML = renderMatchStatsTable(tip);
  } else if (activeTab === 'match-lineup') {
    container.innerHTML = renderMatchLineupList(tip);
  } else if (activeTab === 'match-h2h') {
    container.innerHTML = renderMatchH2HList(tip);
  }
}

function renderMatchesList() {
  document.getElementById('matches').innerHTML = latestMatches.map(renderMatch).join('');
}

function toggleLineupSubs() {
  lineupSubsExpanded = !lineupSubsExpanded;
  renderActiveTabContent();
}

function selectMatch(num) {
  if (selectedMatchNumber !== num) lineupSubsExpanded = false;
  selectedMatchNumber = num;
  const wasOnMatchTab = activeTab !== 'live';

  // Clicking a row makes the Statistik/Händelser tabs appear, but doesn't auto-switch
  // to them — unless we're already viewing a match tab, in which case follow the click,
  // landing on whichever tab actually has data for the new match (falls back to Live
  // if it has none of them).
  if (wasOnMatchTab) {
    const tip = latestMatches.find(m => m.Number === num);
    if (!MATCH_TAB_CHECKS[activeTab](tip)) activeTab = pickMatchTab(tip);
  }

  renderTabs();
  renderMatchesList();
  // Always re-render — even when the fallback above lands back on Live, the panel still
  // needs to swap away from the previous match's stale content right now, not wait for
  // the next 5s poll tick.
  if (wasOnMatchTab || activeTab !== 'live') renderActiveTabContent();
}

// ── Match row ─────────────────────────────────────────────────────────────────

function dedupeLeagueRound(leagueName, leagueRound) {
  if (!leagueName || !leagueRound) return leagueRound || '';

  const separator = ' - ';
  const lastSepIndex = leagueName.lastIndexOf(separator);
  const lastNameSegment = lastSepIndex >= 0 ? leagueName.slice(lastSepIndex + separator.length) : leagueName;

  if (leagueRound.toLowerCase() === lastNameSegment.toLowerCase())
    return '';

  const prefix = lastNameSegment + separator;
  return leagueRound.toLowerCase().startsWith(prefix.toLowerCase())
    ? leagueRound.slice(prefix.length)
    : leagueRound;
}

function renderMatch(m) {
  const status = getStatus(m);
  const result = getResult(m);

  const rowClass = result === 'correct'      ? 'row-correct'
    : result === 'wrong'                     ? 'row-wrong'
    : status === 'live'                      ? 'row-live'
    : status === 'notstarted'                ? 'row-notstarted'
    : '';

  const selectedClass = m.Number === selectedMatchNumber ? 'row-selected' : '';

  const logo = (id, name) => id
    ? `<img class="team-logo" src="https://media.api-sports.io/football/teams/${id}.png" alt="${name}" loading="lazy" onerror="this.style.visibility='hidden'">`
    : `<span class="team-logo-placeholder"></span>`;

  // Some leagues (mainly lower Swedish divisions) repeat the region name in both the league
  // name and the round, e.g. Name "Ettan - Södra" + Round "Södra - 15" would otherwise display
  // as "Ettan - Södra - Södra - 15". Strip the overlapping lead-in from Round when it matches
  // the last " - "-delimited segment of Name — "World Cup" + "Round of 32" has no overlap and
  // passes through unchanged. Display-only: the raw API values in leagueMap are left untouched.

  const league = m.FixtureId != null ? (leagueMap[m.FixtureId] ?? null) : null;
  const round = league ? dedupeLeagueRound(league.Name, league.RoundSwedish ?? league.Round) : '';
  const baseName = league ? league.Name.split(' - ')[0] : '';
  const leagueName = baseName + (round ? ` - ${round}` : '');
  const leagueRow = league
    ? `<div class="match-league"><span class="league-name">${leagueName}</span>${league.Flag ? `<img class="league-flag" src="${league.Flag}" alt="">` : ''}${league.VenueName ? `<span class="league-venue"> · ${league.VenueName}</span>` : ''}</div>`
    : '';

  const EMPTY_LEAGUE_LOGO = 'https://media.api-sports.io/football/leagues/1.png';
  const leagueLogoUrl = league?.Logo && league.Logo !== EMPTY_LEAGUE_LOGO ? league.Logo : null;
  const leagueLogoCol = `<div class="match-league-logo-col">${leagueLogoUrl ? `<img class="match-league-logo" src="${leagueLogoUrl}" alt="">` : ''}</div>`;

  return `
    <div class="match-row ${rowClass} ${selectedClass}" data-num="${m.Number}">
      <div class="match-num">${m.Number}</div>
      <div class="match-info-col">
        <div class="match-teams">
          <span class="team-logo-home">${logo(m.HomeTeamId, m.HomeTeam)}</span>
          <span class="team-name team-name-home">${m.HomeTeam}</span>
          <span class="team-sep">–</span>
          <span class="team-name team-name-away">${m.AwayTeam}</span>
          <span class="team-logo-away">${logo(m.AwayTeamId, m.AwayTeam)}</span>
        </div>
        ${leagueRow}
      </div>
      ${renderResultIcon(result)}
      ${leagueLogoCol}
      ${renderMatchStatus(m, status)}
      ${renderScoreBadge(m, status, result)}
      ${renderTipButtons(m)}
    </div>`;
}

// ── Match sub-components ──────────────────────────────────────────────────────

function renderResultIcon() {
  return `<div class="result-icon"></div>`;
}

function renderMatchStatus(m, status) {
  if (m.StatusShort === 'HT')
    return `<div class="match-status s-live"><span class="live-min">HT</span></div>`;
  // Coupon only counts the 90-minute result — extra time/penalties display as FT here too,
  // mirroring the Discord dashboard (MatchDisplayFormatter.GetStatusDisplay).
  if (m.StatusShort === 'ET' || m.StatusShort === 'BT' || m.StatusShort === 'P')
    return `<div class="match-status">FT</div>`;
  if (status === 'live') {
    const min = m.Extra > 0 ? `${m.Elapsed}+${m.Extra}'` : m.Elapsed > 0 ? `${m.Elapsed}'` : 'LIVE';
    return `<div class="match-status s-live s-live-min"><span class="live-dot"></span><span class="live-min">${min}</span></div>`;
  }
  if (status === 'finished')
    return `<div class="match-status">FT</div>`;
  const { day, time } = formatKickoff(m.KickoffUtc);
  return `<div class="match-status">${day} ${time}</div>`;
}

function renderScoreBadge(m, status, result) {
  if (status === 'notstarted')
    return `<div class="score-badge empty"></div>`;

  let cls;
  if (result === 'correct') {
    cls = 'correct';
  } else if (result === 'wrong') {
    cls = 'wrong';
  } else if (status === 'live') {
    const liveOutcome = m.HomeScore > m.AwayScore ? '1' : m.HomeScore < m.AwayScore ? '2' : 'X';
    const tips = m.Tip ? m.Tip.split('') : [];
    cls = tips.includes(liveOutcome) ? 'correct' : 'wrong';
  } else {
    cls = '';
  }
  return `<div class="score-badge ${cls}">${m.HomeScore} - ${m.AwayScore}</div>`;
}

function renderTipButtons(m) {
  const tips = m.Tip ? m.Tip.split('') : [];
  const pcts = [m.Percentage1, m.PercentageX, m.Percentage2];
  const hasPct = pcts.some(p => p != null);

  const status = getStatus(m);
  const liveOutcome = status === 'live'
    ? (m.HomeScore > m.AwayScore ? '1' : m.HomeScore < m.AwayScore ? '2' : 'X')
    : null;
  const currentOutcome = m.Outcome || liveOutcome;

  const btns = ['1', 'X', '2'].map((opt) => {
    const isTip     = tips.includes(opt);
    const isOutcome = currentOutcome && currentOutcome === opt;
    const cls = [isTip ? 'is-tip' : '', isOutcome ? 'is-outcome' : ''].filter(Boolean).join(' ');
    return `<div class="tip-btn ${cls}">${opt}</div>`;
  }).join('');

  const pctsRow = hasPct
    ? `<div class="tip-pcts-row">${pcts.map(p => `<div class="tip-pct">${p != null ? p + '%' : ''}</div>`).join('')}</div>`
    : '';

  const odds = [m.Odds1, m.OddsX, m.Odds2];
  const hasOdds = odds.some(o => o != null);
  const oddsRow = hasOdds
    ? `<div class="tip-pcts-row tip-odds-row">${odds.map(o => `<div class="tip-pct tip-odds">${o != null ? parseFloat(o).toFixed(2).replace('.', ',') : ''}</div>`).join('')}</div>`
    : '';

  return `<div class="tip-btns">${oddsRow}<div class="tip-btns-row">${btns}</div>${pctsRow}</div>`;
}

// ── Events feed ───────────────────────────────────────────────────────────────

function classifyGoalEvent(e, match) {
  if (!match || !e.Score) return '';
  const parts = e.Score.split(/\s*-\s*/);
  if (parts.length !== 2) return '';
  const newHome = parseInt(parts[0], 10);
  const newAway = parseInt(parts[1], 10);
  if (isNaN(newHome) || isNaN(newAway)) return '';

  const isOwnGoal = e.Detail === 'Own Goal';
  const scorerIsHome = e.TeamId ? e.TeamId === match.HomeTeamId : e.Team === match.HomeTeam;
  const outcome = (h, a) => h > a ? '1' : h < a ? '2' : 'X';
  const newOutcome = outcome(newHome, newAway);
  const tip = match.Tip || '';

  return tip.includes(newOutcome) ? 'ev-good' : 'ev-bad';
}

function renderEventsList(events, fixtureMap) {
  if (!events.length)
    return `<div class="events-empty">Inga händelser ännu</div>`;

  return events
    .slice()
    .sort((a, b) => new Date(b.CreatedUtc) - new Date(a.CreatedUtc))
    .map(e => renderEvent(e, fixtureMap))
    .join('');
}

function getEventPeriod(e) {
  if (e.Elapsed <= 45)  return 'Första halvlek';
  if (e.Elapsed <= 90)  return 'Andra halvlek';
  if (e.Elapsed <= 105) return 'Förlängning 1';
  return 'Förlängning 2';
}

function renderMatchEventsList(events, fixtureMap) {
  if (!events.length)
    return `<div class="events-empty">Inga händelser ännu</div>`;

  const matchEvents = events.filter(e => e.Type !== 'Injury');
  const sorted      = matchEvents.slice().sort((a, b) => {
    const ta = a.Elapsed * 100 + a.Extra;
    const tb = b.Elapsed * 100 + b.Extra;
    if (tb !== ta) return tb - ta;
    return new Date(b.CreatedUtc) - new Date(a.CreatedUtc);
  });

  let html = '';
  let currentPeriod = null;
  for (const e of sorted) {
    const period = getEventPeriod(e);
    if (period !== currentPeriod) {
      html += `<div class="events-period">${period}</div>`;
      currentPeriod = period;
    }
    html += renderEvent(e, fixtureMap);
  }

  return html || `<div class="events-empty">Inga händelser ännu</div>`;
}

function renderEvent(e, fixtureMap) {
  const match = fixtureMap[e.FixtureId];

  const typeClass = e.Type === 'Goal'         ? 'ev-goal'
    : e.Type === 'Card'                       ? 'ev-card'
    : e.Type === 'Substitution'               ? 'ev-subst'
    : e.Type === 'Injury'                     ? 'ev-injury'
    : 'ev-var';

  const beneficial = e.Type === 'Goal' && match
    ? classifyGoalEvent(e, match)
    : e.Text?.includes('✅') ? 'ev-good' : e.Text?.includes('❌') ? 'ev-bad' : '';

  const icon    = eventIcon(e);
  const minute  = e.Extra > 0 ? `${e.Elapsed}+${e.Extra}'` : `${e.Elapsed}'`;
  const minuteHtml = e.Type === 'Injury' ? '' : `<span class="event-time">${minute}</span>`;
  const score   = e.Score ? e.Score.replace(/\s*-\s*/, '–') : '';

  let mainText, subText;

  if (e.Type === 'Goal') {
    const detail = e.Detail === 'Own Goal' ? ' (Självmål)' : e.Detail === 'Penalty' ? ' (Straff)' : '';
    mainText = (e.Player ? e.Player : e.Team ? `Mål för ${e.Team}!` : 'Okänd') + detail;
    let scoreLine;
    if (match && e.Score) {
      const parts = e.Score.split(/\s*-\s*/);
      if (parts.length === 2) {
        const isOwnGoal = e.Detail === 'Own Goal';
        const scorerIsHome = e.TeamId ? e.TeamId === match.HomeTeamId : e.Team === match.HomeTeam;
        const homeGotPoint = isOwnGoal ? !scorerIsHome : scorerIsHome;
        const h = homeGotPoint ? `<span class="goal-new">${parts[0]}</span>` : parts[0];
        const a = homeGotPoint ? parts[1] : `<span class="goal-new">${parts[1]}</span>`;
        scoreLine = `${match.HomeTeam} <span class="ev-score">${h} - ${a}</span> ${match.AwayTeam}`;
      } else {
        scoreLine = `${match.HomeTeam} <span class="ev-score">${score}</span> ${match.AwayTeam}`;
      }
    } else {
      scoreLine = match ? `${match.HomeTeam} <span class="ev-score">${score}</span> ${match.AwayTeam}` : score;
    }
    subText = e.Assist ? `${scoreLine} · Assist: ${e.Assist}` : scoreLine;
  } else if (e.Type === 'CancelledGoal') {
    mainText = 'Mål bortdömt!';
    if (match && e.Score) {
      const parts = e.Score.split(/\s*-\s*/);
      if (parts.length === 2) {
        const isHome = e.TeamId ? e.TeamId === match.HomeTeamId : e.Team === match.HomeTeam;
        const h = isHome ? `<strong>${parts[0]}</strong>` : parts[0];
        const a = isHome ? parts[1] : `<strong>${parts[1]}</strong>`;
        const player = e.Player ? ` · ${e.Player}` : '';
        const reason = e.Detail ? ` · ${formatVarReason(e.Detail)}` : '';
        subText = `${match.HomeTeam} ${h}–${a} ${match.AwayTeam}${player}${reason}`;
      } else {
        subText = match ? `${match.HomeTeam} ${score} ${match.AwayTeam}` : score;
      }
    } else {
      subText = e.Team || '';
    }
  } else if (e.Type === 'Substitution') {
    mainText = `Byte: ${e.Team || ''}`;
    subText  = `UT: ${e.Player || '?'} · IN: ${e.Assist || '?'}`;
  } else if (e.Type === 'Injury') {
    const isSuspension = e.Comments === 'Yellow Cards';
    const label = isSuspension ? 'Avstängd' : e.Detail === 'Missing Fixture' ? 'Missar matchen' : 'Tveksam';
    mainText = e.Player || 'Okänd spelare';
    subText = isSuspension
      ? `${label} · ${e.Team || ''}`
      : e.Comments ? `${label} · ${e.Team || ''} · ${e.Comments}` : `${label} · ${e.Team || ''}`;
  } else {
    mainText = e.Player ? e.Player : 'Okänd';
    subText  = e.Comments ? `${e.Team || ''} · ${e.Comments}` : (e.Team || '');
  }

  return `
    <div class="event-row ${typeClass} ${beneficial}">
      <div class="event-icon">${icon}</div>
      <div class="event-body">
        <div class="event-main">${mainText}${minuteHtml}</div>
        <div class="event-sub">${subText}</div>
      </div>
    </div>`;
}

// ── Per-match statistik ──────────────────────────────────────────────────────

function statBarPercents(hv, av) {
  const h = parseFloat(String(hv ?? '0').replace('%', '')) || 0;
  const a = parseFloat(String(av ?? '0').replace('%', '')) || 0;
  const total = h + a;
  if (total <= 0) return [0, 0];
  return [(h / total) * 100, (a / total) * 100];
}

// API doesn't always supply "Passes %" directly (it's sometimes null even when accurate/total
// are present) — compute it ourselves from the raw counts in that case.
function passAccuracy(team) {
  if (team.PassesPercent != null) return team.PassesPercent;
  const total = parseFloat(team.TotalPasses);
  const accurate = parseFloat(team.PassesAccurate);
  if (!total || isNaN(accurate)) return null;
  return `${Math.round((accurate / total) * 100)}%`;
}

function passFraction(team) {
  if (team.PassesAccurate == null || team.TotalPasses == null) return null;
  return `(${team.PassesAccurate}/${team.TotalPasses})`;
}

function renderMatchInfoSection(tip) {
  const league = tip.FixtureId != null ? (leagueMap[tip.FixtureId] ?? null) : null;
  if (!league) return '';
  const round = dedupeLeagueRound(league.Name, league.RoundSwedish ?? league.Round);
  const leagueFull = `${league.Name}${round ? ` - ${round}` : ''}`;
  const rows = [
    ['Liga',  leagueFull],
    ['Arena', league.VenueName],
  ].filter(([, v]) => v);
  if (!rows.length) return '';
  return `
    <div class="stats-section-header">Matchinfo</div>
    <div class="matchinfo-rows">
      ${rows.map(([label, val]) => `
        <div class="matchinfo-row">
          <span class="matchinfo-label">${label}</span>
          <span class="matchinfo-value">${val}</span>
        </div>`).join('')}
    </div>`;
}

function renderMatchStatsTable(tip) {
  const stats = tip.Statistics;
  if (!stats || !stats.Home || !stats.Away)
    return `<div class="events-empty">Ingen statistik tillgänglig för match #${tip.Number} ännu</div>${renderMatchInfoSection(tip)}`;

  const h = stats.Home, a = stats.Away;

  const topRows = [
    ['Bollinnehav', h.BallPossession, a.BallPossession, false, false],
  ];

  // Skott/på mål/utanför stay grouped with no divider between them;
  // blockerade keeps its border to close the group.
  const shotRows = [
    ['Skott', h.TotalShots, a.TotalShots, false, true],
    ['på mål', h.ShotsOnGoal, a.ShotsOnGoal, true, true],
    ['utanför', h.ShotsOffGoal, a.ShotsOffGoal, true, true],
    ['blockerade', h.BlockedShots, a.BlockedShots, true, false],
  ];

  const otherRows = [
    ['Hörnor', h.CornerKicks, a.CornerKicks, false, false],
    ['Frisparkar', h.Fouls, a.Fouls, false, false],
    ['Offside', h.Offsides, a.Offsides, false, false],
    ['Passningar', passAccuracy(h), passAccuracy(a), false, false, passFraction(h), passFraction(a)],
    ['Gula kort', h.YellowCards, a.YellowCards, false, false],
    ['Röda kort', h.RedCards, a.RedCards, false, false],
    ['Räddningar', h.GoalkeeperSaves, a.GoalkeeperSaves, false, false],
  ];

  const hasData = ([, hv, av]) => hv != null || av != null;
  const visibleTop   = topRows.filter(hasData);
  const visibleShot  = shotRows.filter(hasData);
  const visibleOther = otherRows.filter(hasData);

  if (!visibleTop.length && !visibleShot.length && !visibleOther.length)
    return `<div class="events-empty">Ingen statistik tillgänglig för match #${tip.Number} ännu</div>`;

  const renderRow = ([label, hv, av, sub, noBorder, hvSub, avSub]) => {
    const [hPct, aPct] = statBarPercents(hv, av);
    const hCls = hPct > aPct ? 'stats-bar-fill-majority' : 'stats-bar-fill-minority';
    const aCls = aPct > hPct ? 'stats-bar-fill-majority' : 'stats-bar-fill-minority';
    const hVal = hvSub
      ? `<span class="stats-val-main">${hv ?? '-'}</span><span class="stats-val-sub">${hvSub}</span>`
      : (hv ?? '-');
    const aVal = avSub
      ? `<span class="stats-val-main">${av ?? '-'}</span><span class="stats-val-sub">${avSub}</span>`
      : (av ?? '-');
    return `
      <div class="stats-row ${sub ? 'stats-subrow' : ''}">
        <span class="stats-val ${hvSub ? 'stats-val-stacked' : ''}">${hVal}</span>
        <span class="stats-label">${label}</span>
        <span class="stats-val ${avSub ? 'stats-val-stacked' : ''}">${aVal}</span>
      </div>
      <div class="stats-bar ${sub ? 'stats-bar-sub' : ''} ${noBorder ? 'stats-bar-noborder' : ''}">
        <div class="stats-bar-track stats-bar-track-home"><div class="stats-bar-fill ${hCls}" style="width:${hPct}%"></div></div>
        <div class="stats-bar-track stats-bar-track-away"><div class="stats-bar-fill ${aCls}" style="width:${aPct}%"></div></div>
      </div>`;
  };

  return `
    <div class="match-stats-table">
      <div class="stats-row stats-header">
        <span class="stats-val">${tip.HomeTeam}</span>
        <span class="stats-label"></span>
        <span class="stats-val">${tip.AwayTeam}</span>
      </div>
      ${visibleTop.map(renderRow).join('')}
      ${visibleShot.length  ? `<div class="stats-section-header">Skott</div>${visibleShot.map(renderRow).join('')}`   : ''}
      ${visibleOther.length ? `<div class="stats-section-header">Övrigt</div>${visibleOther.map(renderRow).join('')}` : ''}
    </div>
    ${renderMatchInfoSection(tip)}`;
}

// ── Per-match laguppställning ────────────────────────────────────────────────

function renderLineupRow(homePlayer, awayPlayer) {
  const posTag = p => p?.Position ? `<span class="lineup-pos">${p.Position}</span>` : '';
  const home = homePlayer
    ? `<span class="lineup-num">${homePlayer.Number ?? ''}</span>${posTag(homePlayer)}<span class="lineup-name">${homePlayer.Name}</span>`
    : '';
  const away = awayPlayer
    ? `<span class="lineup-name">${awayPlayer.Name}</span>${posTag(awayPlayer)}<span class="lineup-num">${awayPlayer.Number ?? ''}</span>`
    : '';
  return `
    <div class="lineup-row">
      <div class="lineup-side lineup-side-home">${home}</div>
      <div class="lineup-side lineup-side-away">${away}</div>
    </div>`;
}

function renderMatchLineupList(tip) {
  const home = tip.HomeLineup, away = tip.AwayLineup;
  const injuries = tip.Injuries || [];

  if (!home || !away) {
    if (!injuries.length)
      return `<div class="events-empty">Ingen laguppställning tillgänglig för match #${tip.Number} ännu</div>`;
    let html = `<div class="stats-section-header">Frånvaro</div>`;
    for (const e of injuries) html += renderEvent(e, latestFixtureMap);
    return html;
  }

  const formationRow = (home.Formation || away.Formation)
    ? `<div class="lineup-formations">
        <span>${home.Formation || ''}</span>
        <span class="lineup-formations-label">Formation</span>
        <span>${away.Formation || ''}</span>
      </div>`
    : '';

  const coachInfo = (coach, isAway) => {
    if (!coach?.CoachName) return '';
    const photo = coach.CoachPhoto
      ? `<img class="lineup-coach-photo" src="${coach.CoachPhoto}" alt="" loading="lazy" onerror="this.style.display='none'">`
      : '';
    return isAway ? `${coach.CoachName}${photo}` : `${photo}${coach.CoachName}`;
  };

  const coachRow = (home.CoachName || away.CoachName)
    ? `<div class="lineup-formations lineup-coaches">
        <span>${coachInfo(home, false)}</span>
        <span class="lineup-formations-label">Tränare</span>
        <span>${coachInfo(away, true)}</span>
      </div>`
    : '';

  const startCount = Math.max(home.StartXI.length, away.StartXI.length);
  const startRows = [];
  for (let i = 0; i < startCount; i++)
    startRows.push(renderLineupRow(home.StartXI[i], away.StartXI[i]));

  const posOrder = { G: 0, D: 1, M: 2, F: 3 };
  const sortByPos = p => posOrder[p?.Position ?? ''] ?? 9;
  const sortedHomeSubs = [...home.Substitutes].sort((a, b) => sortByPos(a) - sortByPos(b));
  const sortedAwaySubs = [...away.Substitutes].sort((a, b) => sortByPos(a) - sortByPos(b));

  const subCount = Math.max(sortedHomeSubs.length, sortedAwaySubs.length);
  const subRows = [];
  for (let i = 0; i < subCount; i++)
    subRows.push(renderLineupRow(sortedHomeSubs[i], sortedAwaySubs[i]));

  return `
    <div class="match-stats-table">
      <div class="stats-row stats-header">
        <span class="stats-val">${tip.HomeTeam}</span>
        <span class="stats-label"></span>
        <span class="stats-val">${tip.AwayTeam}</span>
      </div>
      ${formationRow}
      ${coachRow}
      <div class="stats-section-header">Startelva</div>
      ${startRows.join('')}
      ${subRows.length ? `
        <div class="stats-section-header lineup-subs-toggle" onclick="toggleLineupSubs()">
          ${lineupSubsExpanded ? 'Avbytare' : 'Avbytare (klicka för att visa)'}
        </div>
        ${lineupSubsExpanded ? subRows.join('') : ''}` : ''}
      ${injuries.length ? `<div class="stats-section-header">Frånvaro</div>${injuries.map(e => renderEvent(e, latestFixtureMap)).join('')}` : ''}
    </div>`;
}

function renderPreMatch(matches) {
  const bets  = calcValueBets(matches);
  const best  = bets.slice(0, 10);
  const worst = bets.filter(v => v.value < 0).slice(-10).reverse();

  const favs = [];
  const opts = [
    { oddsKey: 'Odds1', pctKey: 'Percentage1', teamFn: m => m.HomeTeam },
    { oddsKey: 'OddsX', pctKey: 'PercentageX', teamFn: () => 'Oavgjort' },
    { oddsKey: 'Odds2', pctKey: 'Percentage2', teamFn: m => m.AwayTeam },
  ];
  for (const m of matches) {
    for (const o of opts) {
      const odds = m[o.oddsKey];
      const pct  = m[o.pctKey];
      if (odds != null) {
        favs.push({ num: m.Number, team: o.teamFn(m), odds: parseFloat(odds), pct });
      }
    }
  }
  favs.sort((a, b) => a.odds - b.odds);
  const topFavs = favs.slice(0, 3);

  const renderSection = (title, rows, renderRow, tooltip = '') => {
    if (!rows.length) return '';
    const tip = tooltip ? ` title="${tooltip}"` : '';
    return `<div class="value-section"><div class="value-title"${tip}>${title}</div>${rows.map(renderRow).join('')}</div>`;
  };

  const valueRow = (v, positive) => `
    <div class="value-row">
      <span class="value-num">#${v.num}</span>
      <span class="value-team">${v.team}</span>
      <span class="value-pct ${positive ? 'green' : 'red'}">${v.value}%</span>
    </div>`;

  const favRow = f => `
    <div class="value-row">
      <span class="value-num">#${f.num}</span>
      <span class="value-team">${f.team}</span>
      <span class="value-pct">${f.odds.toFixed(2).replace('.', ',')}${f.pct != null ? ` · ${f.pct}%` : ''}</span>
    </div>`;

  return renderSection('Största favoriter', topFavs, favRow)
    + `<div class="pre-match-value-row">`
    + renderSection('Bästa streckvärde', best, v => valueRow(v, true), 'Tecknen med bäst streckvärde baserat på streckprocent kontra odds')
    + renderSection('Sämsta streckvärde', worst, v => valueRow(v, false), 'Tecknen med sämst streckvärde baserat på streckprocent kontra odds')
    + `</div>`;
}

// ── H2H tab ───────────────────────────────────────────────────────────────────

function renderMatchH2HList(tip) {
  const matches = tip.H2H;
  if (!matches?.length)
    return `<div class="events-empty">Ingen H2H-data tillgänglig</div>`;

  const sorted = matches.slice().sort((a, b) => new Date(b.Date) - new Date(a.Date));
  let html = '';
  let first = true;

  for (const m of sorted) {
    const date = new Date(m.Date).toLocaleDateString('sv-SE', { day: 'numeric', month: 'short', year: 'numeric' });

    // Swap left/right so coupon's home team is always on the left
    const isSwapped  = tip.HomeTeamId && m.AwayTeamId === tip.HomeTeamId;
    const leftTeam   = isSwapped ? m.AwayTeam     : m.HomeTeam;
    const rightTeam  = isSwapped ? m.HomeTeam     : m.AwayTeam;
    const leftLogo   = isSwapped ? m.AwayTeamLogo : m.HomeTeamLogo;
    const rightLogo  = isSwapped ? m.HomeTeamLogo : m.AwayTeamLogo;
    const leftGoals  = isSwapped ? m.AwayGoals    : m.HomeGoals;
    const rightGoals = isSwapped ? m.HomeGoals    : m.AwayGoals;

    const leftLogoEl  = leftLogo  ? `<img class="h2h-logo" src="${leftLogo}"  alt="">` : `<span class="h2h-logo"></span>`;
    const rightLogoEl = rightLogo ? `<img class="h2h-logo" src="${rightLogo}" alt="">` : `<span class="h2h-logo"></span>`;

    const winClass = leftGoals > rightGoals ? 'h2h-home-win'
                   : rightGoals > leftGoals ? 'h2h-away-win'
                   : 'h2h-draw';

    const leaguePart = m.LeagueLogo
      ? `<img class="h2h-league-logo" src="${m.LeagueLogo}" alt=""> ${m.LeagueName ?? ''}`
      : (m.LeagueName ? m.LeagueName : '');
    html += `<div class="h2h-date-chip${first ? ' h2h-date-chip-first' : ''}"><span class="h2h-chip-date">${date}</span>${leaguePart ? `<span class="h2h-chip-league">${leaguePart}</span>` : ''}</div>
    <div class="h2h-row ${winClass}">
      <div class="h2h-team h2h-home"><span class="h2h-name">${leftTeam}</span>${leftLogoEl}</div>
      <div class="h2h-score">${leftGoals} – ${rightGoals}</div>
      <div class="h2h-team h2h-away">${rightLogoEl}<span class="h2h-name">${rightTeam}</span></div>
    </div>`;
    first = false;
  }
  return html;
}

// ── Stats panel ───────────────────────────────────────────────────────────────

function renderStats(matches, events, payouts) {
  const correct = matches.filter(isCorrect).length;
  const goals   = events.filter(e => e.Type === 'Goal').length;

  const rightCell   = { value: `${correct}/${matches.length}`, label: 'Rätt', colorClass: correct > 0 ? 'green' : '' };
  const secondCell  = payouts.length > 0
    ? { value: payouts[0].Amount, label: `${payouts[0].Correct} rätt`, colorClass: 'green' }
    : { value: goals, label: 'Mål', colorClass: 'blue' };

  const topRow = [rightCell, secondCell].map(s => `
    <div class="stat-cell">
      <div class="stat-value ${s.colorClass}">${s.value}</div>
      <div class="stat-label">${s.label}</div>
    </div>`).join('');

  const payoutSection = payouts.length > 1 ? renderPayoutSection(payouts) : '';

  const bestMoves = calcBestMoves(matches).slice(0, 3);
  const traps     = calcTraps(matches).slice(0, 3);
  const surprises = calcSurprises(matches).slice(0, 3);

  return `
    <div class="stats-top-row">${topRow}</div>
    ${payoutSection}
    <div class="pre-match-value-row">
      ${renderValueSection('Våra bästa drag', bestMoves, true)}
      ${renderValueSection('Fällor', traps, false)}
    </div>
    ${renderValueSection('Största överraskningarna', surprises, null)}`;
}

function renderPayoutSection(payouts) {
  const rows = payouts.slice(0, 4).map(p => `
    <div class="value-row payout-row">
      <span class="value-label">${p.Correct} rätt</span>
      ${p.Rows ? `<span class="value-rows">${p.Rows}</span>` : ''}
      <span class="value-pct green">${p.Amount}</span>
    </div>`).join('');
  return `<div class="value-section"><div class="value-title">Utdelning</div>${rows}</div>`;
}

function calcValueBets(matches) {
  const result = [];
  const opts = [
    { pctKey: 'Percentage1', oddsKey: 'Odds1', teamFn: m => m.HomeTeam },
    { pctKey: 'PercentageX', oddsKey: 'OddsX', teamFn: () => 'Oavgjort' },
    { pctKey: 'Percentage2', oddsKey: 'Odds2', teamFn: m => m.AwayTeam },
  ];
  for (const m of matches) {
    const o1 = parseFloat(m.Odds1), oX = parseFloat(m.OddsX), o2 = parseFloat(m.Odds2);
    const total = (o1 > 0 ? 1/o1 : 0) + (oX > 0 ? 1/oX : 0) + (o2 > 0 ? 1/o2 : 0);
    for (const o of opts) {
      const pct  = m[o.pctKey];
      const odds = parseFloat(m[o.oddsKey]);
      if (pct != null && odds > 0 && total > 0) {
        const fairPct = (1 / odds) / total * 100;
        const value = Math.round(fairPct - pct);
        result.push({ num: m.Number, team: o.teamFn(m), value });
      }
    }
  }
  return result.sort((a, b) => b.value - a.value);
}

function renderValueSection(title, items, positive) {
  if (!items.length) return '';
  const cls = positive === null ? 'amber' : positive ? 'green' : 'red';
  const rows = items.map(v => `
    <div class="value-row">
      <span class="value-num">#${v.num}</span>
      <span class="value-team">${v.team}</span>
      <span class="value-pct ${cls}">${v.value}%</span>
    </div>`).join('');
  return `<div class="value-section"><div class="value-title">${title}</div>${rows}</div>`;
}

// ── Live insights (post-kickoff) ────────────────────────────────────────────

function getOutcomeSymbol(m) {
  if (m.IsFinished && m.Outcome) return m.Outcome;
  return m.HomeScore > m.AwayScore ? '1' : m.HomeScore < m.AwayScore ? '2' : 'X';
}

function outcomeOptions(m) {
  return [
    { sym: '1', pct: m.Percentage1, team: m.HomeTeam },
    { sym: 'X', pct: m.PercentageX, team: 'Oavgjort' },
    { sym: '2', pct: m.Percentage2, team: m.AwayTeam },
  ];
}

// Matches where we're currently correct with the least-backed sign — our best differentiators.
function calcBestMoves(matches) {
  const result = [];
  for (const m of matches) {
    if (!m.IsFinished || !m.Tip) continue;
    const outcome = getOutcomeSymbol(m);
    if (!m.Tip.includes(outcome)) continue;
    const opt = outcomeOptions(m).find(o => o.sym === outcome);
    if (!opt || opt.pct == null) continue;
    result.push({ num: m.Number, team: opt.team, value: opt.pct });
  }
  return result.sort((a, b) => a.value - b.value);
}

// Matches where we backed the crowd favorite and it still failed.
function calcTraps(matches) {
  const result = [];
  for (const m of matches) {
    if (!m.IsFinished || !m.Tip) continue;
    const outcome = getOutcomeSymbol(m);
    const opts = outcomeOptions(m).filter(o => o.pct != null);
    if (!opts.length) continue;
    const favorite = opts.reduce((a, b) => b.pct > a.pct ? b : a);
    if (!m.Tip.includes(favorite.sym) || favorite.sym === outcome) continue;
    result.push({ num: m.Number, team: favorite.team, value: favorite.pct });
  }
  return result.sort((a, b) => b.value - a.value);
}

// Matches where the actual outcome was the least-backed sign, regardless of our tip.
function calcSurprises(matches) {
  const result = [];
  for (const m of matches) {
    if (!m.IsFinished) continue;
    const outcome = getOutcomeSymbol(m);
    const opt = outcomeOptions(m).find(o => o.sym === outcome);
    if (!opt || opt.pct == null) continue;
    result.push({ num: m.Number, team: opt.team, value: opt.pct });
  }
  return result.sort((a, b) => a.value - b.value);
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function isCorrect(m) {
  if (m.IsFinished) return m.Outcome && m.Tip && m.Tip.includes(m.Outcome);
  const lo = m.HomeScore > m.AwayScore ? '1' : m.HomeScore < m.AwayScore ? '2' : 'X';
  return m.Tip && m.Tip.includes(lo);
}

function buildFixtureMap(matches) {
  return Object.fromEntries(matches.map(m => [m.FixtureId, m]));
}

function getStatus(m) {
  if (m.IsFinished) return 'finished';
  if (!m.KickoffUtc) return 'notstarted';
  if (new Date(m.KickoffUtc) > new Date()) return 'notstarted';
  return 'live';
}

function getResult(m) {
  if (!m.IsFinished || !m.Outcome || !m.Tip) return 'pending';
  return m.Tip.includes(m.Outcome) ? 'correct' : 'wrong';
}

function formatVarReason(detail) {
  if (!detail) return '';
  const idx = detail.indexOf(' - ');
  const s = idx >= 0 ? detail.slice(idx + 3) : detail;
  return s.charAt(0).toUpperCase() + s.slice(1);
}

function formatKickoff(utc) {
  if (!utc) return { day: '', time: '–' };

  const d   = new Date(utc);
  const now = new Date();

  const kickoffDay = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  const todayDay   = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const diffDays   = Math.round((kickoffDay - todayDay) / 86400000);

  let day;
  if      (diffDays === 0) day = 'Idag';
  else if (diffDays === 1) day = 'Imorgon';
  else {
    day = d.toLocaleDateString('sv-SE', { weekday: 'long' });
    day = day.charAt(0).toUpperCase() + day.slice(1);
  }

  const time = d.toLocaleTimeString('sv-SE', { hour: '2-digit', minute: '2-digit' });
  return { day, time };
}

function eventIcon(e) {
  const ico = (id, w, h) =>
    `<svg class="ev-icon" width="${w}" height="${h}" aria-hidden="true"><use href="#${id}"/></svg>`;
  if (e.Type === 'Goal') {
    if (e.Detail === 'Own Goal') return ico('icon-ball-og', 16, 16);
    if (e.Detail === 'Penalty')  return ico('icon-penalty',  16, 16);
    return ico('icon-ball', 16, 16);
  }
  if (e.Type === 'Card') {
    if (e.Detail === 'Yellow Card')     return ico('icon-yc',  10, 13);
    if (e.Detail === 'Yellow Red Card') return ico('icon-yrc', 14, 13);
    return ico('icon-rc', 10, 13);
  }
  if (e.Type === 'CancelledGoal') return ico('icon-var', 24, 17);
  if (e.Type === 'Substitution')  return ico('icon-subst', 14, 14);
  if (e.Type === 'Injury')        return e.Comments === 'Yellow Cards' ? ico('icon-suspension', 16, 14) : ico('icon-injury', 14, 14);
  return '';
}

// ── Game type → header class ──────────────────────────────────────────────────

function applyGameClass(gameName) {
  const header = document.querySelector('header');
  const app    = document.getElementById('app');
  const classes = ['game-stryktipset', 'game-europatipset', 'game-topptipset', 'game-annat'];
  header.classList.remove(...classes);
  app.classList.remove(...classes);
  const n = (gameName || '').toLowerCase();
  let cls;
  if      (n.includes('stryktipset'))  cls = 'game-stryktipset';
  else if (n.includes('europatipset')) cls = 'game-europatipset';
  else if (n.includes('topptipset'))   cls = 'game-topptipset';
  else                                  cls = 'game-annat';
  header.classList.add(cls);
  app.classList.add(cls);
}

// ── Light / dark theme ────────────────────────────────────────────────────────

function toggleTheme() {
  const cur  = document.documentElement.dataset.theme || 'dark';
  const next = cur === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = next;
  localStorage.setItem('plingbot-theme', next);
  updateThemeBtn(next);
}

function updateThemeBtn(theme) {
  const btn = document.getElementById('theme-toggle');
  if (btn) btn.textContent = theme === 'dark' ? '☀' : '☾';
}

// ── Bootstrap ─────────────────────────────────────────────────────────────────

document.getElementById('matches').addEventListener('click', e => {
  const row = e.target.closest('.match-row');
  if (!row) return;
  const num = parseInt(row.dataset.num, 10);
  if (!isNaN(num)) selectMatch(num);
});

document.getElementById('panel-tabs').addEventListener('click', e => {
  const btn = e.target.closest('[data-tab]');
  if (!btn) return;
  if (btn.dataset.tab === 'close') {
    selectedMatchNumber = null;
    activeTab = 'live';
    renderMatchesList();
  } else {
    activeTab = btn.dataset.tab;
  }
  renderTabs();
  renderActiveTabContent();
});

const savedTheme = localStorage.getItem('plingbot-theme') || 'dark';
document.documentElement.dataset.theme = savedTheme;
updateThemeBtn(savedTheme);

refresh();
setInterval(refresh, 5000);
