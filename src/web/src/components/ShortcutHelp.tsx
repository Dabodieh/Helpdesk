import { SHORTCUT_HELP } from '../lib/shortcuts'
import Dialog from './Dialog'

export default function ShortcutHelp({ onClose }: { onClose: () => void }) {
  return (
    <Dialog title="Keyboard shortcuts" onClose={onClose}>
      <p className="muted small">Shortcuts are off while you are typing in a field.</p>
      <table className="plain-table">
        <caption className="sr-only">Keyboard shortcuts</caption>
        <thead>
          <tr>
            <th scope="col">Keys</th>
            <th scope="col">Action</th>
          </tr>
        </thead>
        <tbody>
          {SHORTCUT_HELP.map((s) => (
            <tr key={s.keys}>
              <th scope="row">
                <kbd>{s.keys}</kbd>
              </th>
              <td>{s.action}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Dialog>
  )
}
