// Shared by the switcher and the Server screen — both surface a Managed
// profile's PortConflict and need the same one-click fix: change the port
// without navigating away to hand-edit server.properties.
import { h, toast, modal } from '../ui.js';
import { api } from '../api.js';
import { hydrateServerList } from '../store.js';
import { emit } from '../bus.js';

/** Opens a small modal to change one Managed server's server-port. */
export function changeServerPort(server) {
  const input = h('input', {
    class: 'input', type: 'number', min: 1, max: 65535,
    value: String(server.port ?? 25565),
  });

  modal({
    title: `Change port — “${server.name}”`,
    body: h('div', {},
      h('div', { class: 'field' },
        h('label', {}, 'Server port'),
        input,
        h('span', { class: 'hint' },
          'Written to server.properties. Takes effect the next time this server starts.'))),
    actions: [
      { label: 'Cancel', kind: 'ghost' },
      {
        label: 'Save',
        kind: 'primary',
        onClick: async () => {
          const port = parseInt(input.value, 10);
          if (!port || port < 1 || port > 65535) {
            toast('Enter a port between 1 and 65535.', 'err');
            return false;
          }
          try {
            await api.put(`/api/servers/${server.id}/server-port`, { port });
            toast(`Port changed to ${port}`);
            await hydrateServerList();
            // hydrateServerList() only updates state — every view showing
            // servers[] (the switcher, the Server screen) refreshes off this,
            // the same way SSE-driven server-list changes already do.
            emit('store:servers');
          } catch (err) { toast(err.message, 'err'); return false; }
        },
      },
    ],
  });
}
