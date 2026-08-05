// Inline SVG icon set (24×24, stroke-based). Zero external dependencies.
const PATHS = {
  users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
  alert: '<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>',
  box: '<path d="M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z"/><polyline points="3.29 7 12 12 20.71 7"/><line x1="12" y1="22" x2="12" y2="12"/>',
  file: '<path d="M14.5 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7.5L14.5 2z"/><polyline points="14 2 14 8 20 8"/>',
  archive: '<rect x="2" y="3" width="20" height="5" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8"/><path d="M10 12h4"/>',
  clock: '<circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>',
  sliders: '<line x1="21" y1="4" x2="14" y2="4"/><line x1="10" y1="4" x2="3" y2="4"/><line x1="21" y1="12" x2="12" y2="12"/><line x1="8" y1="12" x2="3" y2="12"/><line x1="21" y1="20" x2="16" y2="20"/><line x1="12" y1="20" x2="3" y2="20"/><line x1="14" y1="2" x2="14" y2="6"/><line x1="8" y1="10" x2="8" y2="14"/><line x1="16" y1="18" x2="16" y2="22"/>',
  play: '<polygon points="6 3 20 12 6 21 6 3"/>',
  stop: '<rect x="5" y="5" width="14" height="14" rx="2"/>',
  refresh: '<path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/>',
  plus: '<line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/>',
  trash: '<polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>',
  x: '<line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/>',
  check: '<polyline points="20 6 9 17 4 12"/>',
  chevronDown: '<polyline points="6 9 12 15 18 9"/>',
  search: '<circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>',
  download: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/>',
  folder: '<path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>',
  copy: '<rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>',
  pencil: '<path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/>',
  save: '<path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/><polyline points="17 21 17 13 7 13 7 21"/><polyline points="7 3 7 8 15 8"/>',
  ban: '<circle cx="12" cy="12" r="10"/><line x1="4.93" y1="4.93" x2="19.07" y2="19.07"/>',
  arrowDown: '<line x1="12" y1="5" x2="12" y2="19"/><polyline points="19 12 12 19 5 12"/>',
  arrowUp: '<line x1="12" y1="19" x2="12" y2="5"/><polyline points="5 12 12 5 19 12"/>',
  userX: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><line x1="17" y1="8" x2="22" y2="13"/><line x1="22" y1="8" x2="17" y2="13"/>',
  power: '<path d="M18.36 6.64a9 9 0 1 1-12.73 0"/><line x1="12" y1="2" x2="12" y2="12"/>',
  zap: '<polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"/>',
  globe: '<circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>',
  eraser: '<path d="m7 21-4.3-4.3c-1-1-1-2.5 0-3.4l9.6-9.6c1-1 2.5-1 3.4 0l5.6 5.6c1 1 1 2.5 0 3.4L13 21"/><path d="M22 21H7"/>',
  info: '<circle cx="12" cy="12" r="10"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/>',
};

// Nocturne refresh glyphs (Phosphor-equivalent, hand-drawn to the same 24×24
// stroke wrapper rather than vendoring the ~1MB Phosphor webfont — see
// README "Assets"). Names below match the `ph-*` names used in SCREENS.md;
// a few are visually identical to an existing glyph and just point at it.
Object.assign(PATHS, {
  cube: '<path d="M12 2 20 6.5V17.5L12 22 4 17.5V6.5Z"/><path d="M12 12 20 6.5"/><path d="M12 12 4 6.5"/><path d="M12 12V22"/>',
  caretDown: PATHS.chevronDown,
  bell: '<path d="M6 8a6 6 0 0 1 12 0c0 6.5 2.5 8.5 2.5 8.5h-17S6 14.5 6 8Z"/><path d="M13.7 20.5a2 2 0 0 1-3.4 0"/>',
  terminalWindow: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M7 9l3 3-3 3"/><line x1="12" y1="15" x2="17" y2="15"/>',
  pulse: '<polyline points="2 12 7 12 10 20 14 4 17 12 22 12"/>',
  usersThree: '<circle cx="12" cy="7" r="3"/><path d="M6.5 21v-1a5.5 5.5 0 0 1 11 0v1"/><circle cx="4" cy="9.5" r="2"/><path d="M1 21v-.6a3.3 3.3 0 0 1 2.8-3.26"/><circle cx="20" cy="9.5" r="2"/><path d="M23 21v-.6a3.3 3.3 0 0 0-2.8-3.26"/>',
  warningCircle: '<circle cx="12" cy="12" r="9"/><line x1="12" y1="8" x2="12" y2="13"/><line x1="12" y1="16.3" x2="12" y2="16.31"/>',
  hardDrives: '<rect x="2" y="4" width="20" height="7" rx="2"/><rect x="2" y="13" width="20" height="7" rx="2"/><circle cx="6" cy="7.5" r=".9"/><circle cx="6" cy="16.5" r=".9"/>',
  puzzlePiece: '<path d="M9 4h4v1.6a1.4 1.4 0 0 0 2.8 0V4h4v4h-1.6a1.4 1.4 0 0 0 0 2.8H20v4h-1.6a1.4 1.4 0 0 0 0 2.8H20v4h-4v-1.6a1.4 1.4 0 0 0-2.8 0V20H9v-4H7.4a1.4 1.4 0 0 1 0-2.8H9v-2.4H7.4a1.4 1.4 0 0 1 0-2.8H9Z"/>',
  fileCode: '<path d="M14.5 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7.5L14.5 2z"/><polyline points="14 2 14 8 20 8"/><polyline points="9.5 13 7.5 15 9.5 17"/><polyline points="14.5 13 16.5 15 14.5 17"/>',
  clockCountdown: '<circle cx="12" cy="12" r="9"/><polyline points="12 7 12 12 15.5 13.5"/>',
  slidersHorizontal: PATHS.sliders,
  paperPlaneRight: '<path d="M3 11 21 3l-8 18-2.5-7.5L3 11Z"/><line x1="10.5" y1="13.5" x2="21" y2="3"/>',
  magnifyingGlass: PATHS.search,
  arrowClockwise: PATHS.refresh,
  userMinus: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><line x1="17" y1="11" x2="23" y2="11"/>',
  prohibit: PATHS.ban,
  pencilSimple: PATHS.pencil,
  downloadSimple: PATHS.download,
  memory: '<rect x="3" y="8" width="18" height="10" rx="1"/><line x1="7" y1="8" x2="7" y2="5"/><line x1="11" y1="8" x2="11" y2="5"/><line x1="15" y1="8" x2="15" y2="5"/><line x1="7" y1="18" x2="7" y2="21"/><line x1="17" y1="18" x2="17" y2="21"/>',
  lightning: PATHS.zap,
  checkCircle: '<circle cx="12" cy="12" r="9"/><polyline points="8 12.5 11 15.5 16 9"/>',
});

export function icon(name, cls = 'icon') {
  const span = document.createElement('span');
  span.className = cls;
  span.innerHTML =
    `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" ` +
    `stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${PATHS[name] ?? PATHS.box}</svg>`;
  return span;
}
