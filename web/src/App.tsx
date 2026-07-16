import { FormEvent, useEffect, useState } from 'react'
import {
  Conversation,
  createConversation,
  getHealth,
  getMe,
  listConversations,
} from './api'
import './App.css'

const navItems = [
  'Today',
  'Inbox',
  'Assistant',
  'Tasks',
  'Projects',
  'Customers',
  'Approvals',
  'Admin',
]

export default function App() {
  const [active, setActive] = useState('Inbox')
  const [health, setHealth] = useState('checking…')
  const [userLabel, setUserLabel] = useState('Loading…')
  const [conversations, setConversations] = useState<Conversation[]>([])
  const [subject, setSubject] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const refresh = async () => {
    setError(null)
    try {
      const [healthResult, me, inbox] = await Promise.all([
        getHealth(),
        getMe(),
        listConversations(),
      ])
      setHealth(`${healthResult.status} · ${healthResult.service}`)
      setUserLabel(`${me.displayName} · ${me.authMode}`)
      setConversations(inbox)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to reach API')
      setHealth('offline')
    }
  }

  useEffect(() => {
    void refresh()
  }, [])

  const onCreate = async (event: FormEvent) => {
    event.preventDefault()
    if (!subject.trim()) return
    setBusy(true)
    setError(null)
    try {
      await createConversation(subject.trim())
      setSubject('')
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not create conversation')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="shell">
      <aside className="rail">
        <div className="brand">
          <span className="brand-mark">P</span>
          <div>
            <strong>Palantir</strong>
            <p>Sable pilot</p>
          </div>
        </div>
        <nav>
          {navItems.map((item) => (
            <button
              key={item}
              type="button"
              className={item === active ? 'nav active' : 'nav'}
              onClick={() => setActive(item)}
            >
              {item}
            </button>
          ))}
        </nav>
      </aside>

      <main className="stage">
        <header className="topbar">
          <div>
            <p className="eyebrow">{active}</p>
            <h1>Unified inbox</h1>
          </div>
          <div className="meta">
            <span>{userLabel}</span>
            <span className={health.startsWith('ok') ? 'pill ok' : 'pill'}>{health}</span>
          </div>
        </header>

        {active !== 'Inbox' ? (
          <section className="panel placeholder">
            <h2>{active}</h2>
            <p>Shell placeholder — Phase 1 foundation focuses on inbox + API wiring.</p>
          </section>
        ) : (
          <section className="inbox">
            <form className="composer" onSubmit={onCreate}>
              <input
                value={subject}
                onChange={(e) => setSubject(e.target.value)}
                placeholder="Start a conversation subject…"
                aria-label="Conversation subject"
              />
              <button type="submit" disabled={busy}>
                {busy ? 'Creating…' : 'New conversation'}
              </button>
            </form>

            {error && <p className="error">{error}</p>}

            <div className="list">
              {conversations.length === 0 ? (
                <div className="empty">
                  <h2>No conversations yet</h2>
                  <p>Create one above to exercise the conversations API.</p>
                </div>
              ) : (
                conversations.map((item) => (
                  <article key={item.id} className="row">
                    <div>
                      <h3>{item.subject || 'Untitled conversation'}</h3>
                      <p>
                        {item.channel} · {item.status}
                      </p>
                    </div>
                    <time dateTime={item.updatedAt}>
                      {new Date(item.updatedAt).toLocaleString()}
                    </time>
                  </article>
                ))
              )}
            </div>
          </section>
        )}
      </main>
    </div>
  )
}
