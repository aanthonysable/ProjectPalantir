import { FormEvent, useEffect, useState } from 'react'
import {
  Conversation,
  Message,
  TaskItem,
  DEMO_USER_ID,
  addMessage,
  claimConversation,
  completeTask,
  createConversation,
  createTask,
  getHealth,
  getMe,
  listConversations,
  listMessages,
  listTasks,
  releaseConversation,
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

function assigneeLabel(conversation: Conversation) {
  if (!conversation.assignedUserId) return 'Unassigned'
  if (conversation.assignedUserId === DEMO_USER_ID) return 'Assigned to you'
  return `Assigned · ${conversation.assignedUserId.slice(0, 8)}`
}

export default function App() {
  const [active, setActive] = useState('Inbox')
  const [health, setHealth] = useState('checking…')
  const [userLabel, setUserLabel] = useState('Loading…')
  const [conversations, setConversations] = useState<Conversation[]>([])
  const [tasks, setTasks] = useState<TaskItem[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [subject, setSubject] = useState('')
  const [messageBody, setMessageBody] = useState('')
  const [asNote, setAsNote] = useState(false)
  const [taskTitle, setTaskTitle] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const selected = conversations.find((c) => c.id === selectedId) ?? null

  const refreshInbox = async () => {
    const inbox = await listConversations()
    setConversations(inbox)
    return inbox
  }

  const refreshTasks = async () => {
    setTasks(await listTasks())
  }

  const refresh = async () => {
    setError(null)
    try {
      const [healthResult, me] = await Promise.all([getHealth(), getMe()])
      setHealth(`${healthResult.status} · ${healthResult.service}`)
      setUserLabel(`${me.displayName} · ${me.authMode}`)
      await Promise.all([refreshInbox(), refreshTasks()])
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to reach API')
      setHealth('offline')
    }
  }

  const loadMessages = async (conversationId: string) => {
    setMessages(await listMessages(conversationId))
  }

  useEffect(() => {
    void refresh()
  }, [])

  useEffect(() => {
    if (!selectedId) {
      setMessages([])
      return
    }
    void loadMessages(selectedId).catch((err) =>
      setError(err instanceof Error ? err.message : 'Could not load messages'),
    )
  }, [selectedId])

  const onCreate = async (event: FormEvent) => {
    event.preventDefault()
    if (!subject.trim()) return
    setBusy(true)
    setError(null)
    try {
      const created = await createConversation(subject.trim())
      setSubject('')
      await refreshInbox()
      setSelectedId(created.id)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not create conversation')
    } finally {
      setBusy(false)
    }
  }

  const onSendMessage = async (event: FormEvent) => {
    event.preventDefault()
    if (!selectedId || !messageBody.trim()) return
    setBusy(true)
    setError(null)
    try {
      await addMessage(selectedId, messageBody.trim(), { isInternalNote: asNote })
      setMessageBody('')
      setAsNote(false)
      await Promise.all([loadMessages(selectedId), refreshInbox()])
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not send message')
    } finally {
      setBusy(false)
    }
  }

  const onClaim = async () => {
    if (!selectedId) return
    setBusy(true)
    setError(null)
    try {
      await claimConversation(selectedId)
      await refreshInbox()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not claim conversation')
    } finally {
      setBusy(false)
    }
  }

  const onRelease = async () => {
    if (!selectedId) return
    setBusy(true)
    setError(null)
    try {
      await releaseConversation(selectedId)
      await refreshInbox()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not release conversation')
    } finally {
      setBusy(false)
    }
  }

  const onCreateTask = async (event: FormEvent) => {
    event.preventDefault()
    if (!taskTitle.trim()) return
    setBusy(true)
    setError(null)
    try {
      await createTask(taskTitle.trim())
      setTaskTitle('')
      await refreshTasks()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not create task')
    } finally {
      setBusy(false)
    }
  }

  const onCompleteTask = async (taskId: string) => {
    setBusy(true)
    setError(null)
    try {
      await completeTask(taskId)
      await refreshTasks()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not complete task')
    } finally {
      setBusy(false)
    }
  }

  const title =
    active === 'Inbox' ? 'Unified inbox' : active === 'Tasks' ? 'Tasks & reminders' : active

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
            <h1>{title}</h1>
          </div>
          <div className="meta">
            <span>{userLabel}</span>
            <span className={health.startsWith('ok') ? 'pill ok' : 'pill'}>{health}</span>
          </div>
        </header>

        {error && <p className="error">{error}</p>}

        {active === 'Inbox' && (
          <section className="inbox-layout">
            <div className="inbox-list">
              <form className="composer" onSubmit={onCreate}>
                <input
                  value={subject}
                  onChange={(e) => setSubject(e.target.value)}
                  placeholder="Start a conversation subject…"
                  aria-label="Conversation subject"
                />
                <button type="submit" disabled={busy}>
                  {busy ? 'Creating…' : 'New'}
                </button>
              </form>

              <div className="list">
                {conversations.length === 0 ? (
                  <div className="empty">
                    <h2>No conversations yet</h2>
                    <p>Create one above, then claim it and add messages or notes.</p>
                  </div>
                ) : (
                  conversations.map((item) => (
                    <button
                      key={item.id}
                      type="button"
                      className={item.id === selectedId ? 'row selectable active' : 'row selectable'}
                      onClick={() => setSelectedId(item.id)}
                    >
                      <div>
                        <h3>{item.subject || 'Untitled conversation'}</h3>
                        <p>
                          {item.channel} · {item.status} · {assigneeLabel(item)}
                        </p>
                      </div>
                      <time dateTime={item.updatedAt}>
                        {new Date(item.updatedAt).toLocaleString()}
                      </time>
                    </button>
                  ))
                )}
              </div>
            </div>

            <div className="thread">
              {!selected ? (
                <div className="empty">
                  <h2>Select a conversation</h2>
                  <p>Messages, internal notes, and claim/release live here.</p>
                </div>
              ) : (
                <>
                  <div className="thread-header">
                    <div>
                      <h2>{selected.subject || 'Untitled conversation'}</h2>
                      <p>{assigneeLabel(selected)}</p>
                    </div>
                    <div className="actions">
                      {!selected.assignedUserId || selected.assignedUserId !== DEMO_USER_ID ? (
                        <button type="button" onClick={onClaim} disabled={busy}>
                          Claim
                        </button>
                      ) : (
                        <button type="button" className="ghost" onClick={onRelease} disabled={busy}>
                          Release
                        </button>
                      )}
                    </div>
                  </div>

                  <div className="timeline">
                    {messages.length === 0 ? (
                      <p className="muted">No messages yet.</p>
                    ) : (
                      messages.map((msg) => (
                        <article
                          key={msg.id}
                          className={msg.isInternalNote ? 'bubble note' : 'bubble'}
                        >
                          <header>
                            <span>{msg.isInternalNote ? 'Internal note' : msg.direction}</span>
                            <time dateTime={msg.createdAt}>
                              {new Date(msg.createdAt).toLocaleString()}
                            </time>
                          </header>
                          <p>{msg.body}</p>
                        </article>
                      ))
                    )}
                  </div>

                  <form className="composer thread-composer" onSubmit={onSendMessage}>
                    <input
                      value={messageBody}
                      onChange={(e) => setMessageBody(e.target.value)}
                      placeholder={asNote ? 'Add an internal note…' : 'Write a reply…'}
                      aria-label="Message body"
                    />
                    <label className="check">
                      <input
                        type="checkbox"
                        checked={asNote}
                        onChange={(e) => setAsNote(e.target.checked)}
                      />
                      Note
                    </label>
                    <button type="submit" disabled={busy}>
                      Send
                    </button>
                  </form>
                </>
              )}
            </div>
          </section>
        )}

        {active === 'Tasks' && (
          <section className="tasks">
            <form className="composer" onSubmit={onCreateTask}>
              <input
                value={taskTitle}
                onChange={(e) => setTaskTitle(e.target.value)}
                placeholder="Remind someone or create a task…"
                aria-label="Task title"
              />
              <button type="submit" disabled={busy}>
                Add task
              </button>
            </form>

            <div className="list">
              {tasks.length === 0 ? (
                <div className="empty">
                  <h2>No tasks yet</h2>
                  <p>Create a reminder or follow-up for the pilot user.</p>
                </div>
              ) : (
                tasks.map((task) => (
                  <article key={task.id} className="row task-row">
                    <div>
                      <h3 className={task.status === 'Completed' ? 'done' : undefined}>
                        {task.title}
                      </h3>
                      <p>
                        {task.status} · {task.priority}
                        {task.dueAt ? ` · due ${new Date(task.dueAt).toLocaleString()}` : ''}
                      </p>
                    </div>
                    {task.status !== 'Completed' && (
                      <button type="button" onClick={() => void onCompleteTask(task.id)} disabled={busy}>
                        Complete
                      </button>
                    )}
                  </article>
                ))
              )}
            </div>
          </section>
        )}

        {active !== 'Inbox' && active !== 'Tasks' && (
          <section className="panel placeholder">
            <h2>{active}</h2>
            <p>Coming in a later phase — Phase 2 covers inbox messaging and tasks.</p>
          </section>
        )}
      </main>
    </div>
  )
}
