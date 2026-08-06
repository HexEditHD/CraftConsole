// BLUEPRINT icon set — 24x24, 1.5px stroke, round joins, butt caps.
// Drawn as schematic parts: even stroke weight, no tapering, geometry
// that would survive being printed at 1:1 on a drawing sheet.
const P = {
  cube:      '<path d="M12 2.5 20.5 7.25v9.5L12 21.5 3.5 16.75v-9.5Z"/><path d="M12 12 20.5 7.25"/><path d="M12 12 3.5 7.25"/><path d="M12 12v9.5"/>',
  terminal:  '<rect x="3" y="4.5" width="18" height="15" rx="1"/><path d="M7 9.5 9.75 12 7 14.5"/><path d="M12.5 14.5h4.5"/>',
  pulse:     '<path d="M2 12h4.5l2.75 7 4-14 2.75 7H22"/>',
  users:     '<path d="M3.5 20.5v-1.5a4.5 4.5 0 0 1 4.5-4.5h3a4.5 4.5 0 0 1 4.5 4.5v1.5"/><circle cx="9.5" cy="7.5" r="3.5"/><path d="M17 14.5a4 4 0 0 1 3.5 4v2"/>',
  warning:   '<path d="M12 3 21.5 20H2.5Z"/><path d="M12 9.5v4.5"/><path d="M12 16.75v.01"/>',
  drives:    '<rect x="3" y="4.5" width="18" height="6" rx="1"/><rect x="3" y="13.5" width="18" height="6" rx="1"/><path d="M6.5 7.5h.01"/><path d="M6.5 16.5h.01"/>',
  puzzle:    '<path d="M4.5 4.5h5v1.5a2 2 0 0 0 4 0V4.5h5v5H17a2 2 0 0 0 0 4h1.5v5h-5V17a2 2 0 0 0-4 0v1.5h-5v-5H6a2 2 0 0 0 0-4H4.5Z"/>',
  file:      '<path d="M5.5 2.5h8l5 5v14h-13Z"/><path d="M13.5 2.5v5h5"/><path d="M9.5 13 7.5 15l2 2"/><path d="M14 13l2 2-2 2"/>',
  archive:   '<rect x="3" y="3.5" width="18" height="4.5" rx="1"/><path d="M4.75 8v11.5a1 1 0 0 0 1 1h12.5a1 1 0 0 0 1-1V8"/><path d="M10 11.75h4"/>',
  clock:     '<circle cx="12" cy="12" r="9"/><path d="M12 6.75V12l3.5 2"/>',
  sliders:   '<path d="M3 7h4.5"/><path d="M12.5 7H21"/><path d="M3 12h8.5"/><path d="M16.5 12H21"/><path d="M3 17h2.5"/><path d="M10.5 17H21"/><rect x="7.5" y="4.75" width="5" height="4.5" rx="1"/><rect x="11.5" y="9.75" width="5" height="4.5" rx="1"/><rect x="5.5" y="14.75" width="5" height="4.5" rx="1"/>',

  play:      '<path d="M7 4.75 19 12 7 19.25Z"/>',
  stop:      '<rect x="6" y="6" width="12" height="12" rx="1"/>',
  restart:   '<path d="M3 12a9 9 0 1 0 2.75-6.5L3 8"/><path d="M3 3.5V8h4.5"/>',
  power:     '<path d="M18 6.5a8.5 8.5 0 1 1-12 0"/><path d="M12 2.5V12"/>',
  plus:      '<path d="M12 4.5v15"/><path d="M4.5 12h15"/>',
  trash:     '<path d="M4 6.5h16"/><path d="M6.5 6.5v13a1 1 0 0 0 1 1h9a1 1 0 0 0 1-1v-13"/><path d="M9.5 6.5v-3h5v3"/><path d="M10 11v5.5"/><path d="M14 11v5.5"/>',
  x:         '<path d="M5.5 5.5 18.5 18.5"/><path d="M18.5 5.5 5.5 18.5"/>',
  check:     '<path d="M4.5 12.5 9.5 17.5 19.5 6.5"/>',
  search:    '<circle cx="10.5" cy="10.5" r="7"/><path d="M15.5 15.5 21 21"/>',
  eraser:    '<path d="M8.5 20.5 3.5 15.5a1 1 0 0 1 0-1.4L13 4.6a1 1 0 0 1 1.4 0l5 5a1 1 0 0 1 0 1.4l-9.5 9.5Z"/><path d="M20.5 20.5H8.5"/>',
  send:      '<path d="M2.5 11.5 21.5 3l-8.5 18.5-2.5-8Z"/><path d="M10.5 13.5 21.5 3"/>',
  arrowDown: '<path d="M12 4v15.5"/><path d="M5.5 13 12 19.5 18.5 13"/>',
  copy:      '<rect x="8.5" y="8.5" width="12" height="12" rx="1"/><path d="M15.5 8.5v-4a1 1 0 0 0-1-1h-10a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h4"/>',
  pencil:    '<path d="M4 20h4L20 8l-4-4L4 16Z"/><path d="M14.5 5.5 18.5 9.5"/>',
  folder:    '<path d="M3 5.5a1 1 0 0 1 1-1h4.5l2 3H20a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1Z"/>',
  download:  '<path d="M12 3v12.5"/><path d="M7 11l5 5 5-5"/><path d="M4 20.5h16"/>',

  userMinus: '<path d="M3.5 20.5v-1.5a4.5 4.5 0 0 1 4.5-4.5h3a4.5 4.5 0 0 1 4.5 4.5v1.5"/><circle cx="9.5" cy="7.5" r="3.5"/><path d="M17.5 8.5h4"/>',
  ban:       '<circle cx="12" cy="12" r="9"/><path d="M5.6 5.6 18.4 18.4"/>',

  bolt:      '<path d="M13.5 2.5 4.5 14h7l-1 7.5L20 10h-7Z"/>',
  memory:    '<rect x="3" y="7.5" width="18" height="10" rx="1"/><path d="M7 7.5v-4"/><path d="M12 7.5v-4"/><path d="M17 7.5v-4"/><path d="M7 17.5v3"/><path d="M17 17.5v3"/>',
  info:      '<circle cx="12" cy="12" r="9"/><path d="M12 11v6"/><path d="M12 7.75v.01"/>',
};

// Aliases for the six views outside the redesign slice, which still call
// the previous icon names. Drop once those are rebuilt in this language.
Object.assign(P, {
  refresh: P.restart,     arrowClockwise: P.restart,
  alert: P.warning,       warningCircle: P.warning,
  checkCircle: P.check,   magnifyingGlass: P.search,
  paperPlaneRight: P.send, downloadSimple: P.download,
  lightning: P.bolt,      usersThree: P.users,
  hardDrives: P.drives,   terminalWindow: P.terminal,
  fileCode: P.file,       fileText: P.file,
  puzzlePiece: P.puzzle,  clockCountdown: P.clock,
  slidersHorizontal: P.sliders,
  pencilSimple: P.pencil, userX: P.userMinus,
  prohibit: P.ban,        caretDown: P.arrowDown,
  box: P.cube,            gauge: P.pulse,
  save: P.check,          chevronDown: P.arrowDown,
});

export function icon(name, cls = 'icon') {
  const span = document.createElement('span');
  span.className = cls;
  span.innerHTML =
    `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" ` +
    `stroke-linecap="butt" stroke-linejoin="round" aria-hidden="true">${P[name] ?? P.cube}</svg>`;
  return span;
}
