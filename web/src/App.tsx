import { FormEvent, useEffect, useState } from 'react'
import {
  ApiError,
  ApprovalItem,
  ConnectedAccount,
  Conversation,
  Message,
  OutlookMessage,
  SessionUser,
  TaskItem,
  addMessage,
  approveRequest,
  beginMicrosoftAuthorize,
  claimConversation,
  clearSession,
  completeTask,
  createConversation,
  createReplyForApproval,
  createTask,
  disconnectAccount,
  getAccessToken,
  getHealth,
  getMe,
  getStoredSession,
  listApprovals,
  listConnectedAccounts,
  listConversations,
  listMessages,
  listOutlookMail,
  listTasks,
  login,
  logout,
  registerPilotUser,
  rejectRequest,
  releaseConversation,
  draftReplyWithAi,
  summarizeConversation,
  syncOutlookInbox,
} from './api'
import './App.css'

const readyNavItems = ['Inbox', 'Tasks', 'Approvals', 'Admin'] as const
const soonNavItems = ['Today', 'Assistant', 'Projects', 'Customers'] as const
const navItems = [...readyNavItems, ...soonNavItems] as const
type NavItem = (typeof navItems)[number]

function isReadyNav(item: NavItem): item is (typeof readyNavItems)[number] {
  return (readyNavItems as readonly string[]).includes(item)
}

function assigneeLabel(conversation: Conversation, currentUserId: string | null) {
  if (!conversation.assignedUserId) return 'Unassigned'
  if (currentUserId && conversation.assignedUserId === currentUserId) return 'Assigned to you'
  return `Assigned · ${conversation.assignedUserId.slice(0, 8)}`
}

export default function App() {
  const [session, setSession] = useState<SessionUser | null>(() => getStoredSession())
  const [loginEmail, setLoginEmail] = useState('alec.anthony@dnow.com')
  const [loginPassword, setLoginPassword] = useState('pilot-demo')
  const [showRegister, setShowRegister] = useState(false)
  const [registerName, setRegisterName] = useState('')
  const [registerEmail, setRegisterEmail] = useState('')
  const [registerPassword, setRegisterPassword] = useState('')
  const [active, setActive] = useState<NavItem>('Inbox')
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
  const [accounts, setAccounts] = useState<ConnectedAccount[]>([])
  const [outlookMail, setOutlookMail] = useState<OutlookMessage[]>([])
  const [approvals, setApprovals] = useState<ApprovalItem[]>([])
  const [statusBanner, setStatusBanner] = useState<string | null>(null)

  const selected = conversations.find((c) => c.id === selectedId) ?? null
  const currentUserId = session?.userId ?? null

  const refreshInbox = async () => {
    const inbox = await listConversations()
    setConversations(inbox)
    return inbox
  }

  const refreshTasks = async () => {
    setTasks(await listTasks())
  }

  const refreshAccounts = async () => {
    const items = await listConnectedAccounts()
    setAccounts(items)
    const connected = items.find(
      (a) => a.connectionStatus === 'Connected' || a.connectionStatus === '1',
    )
    if (connected) {
      setOutlookMail(await listOutlookMail(connected.id))
    } else {
      setOutlookMail([])
    }
  }

  const refreshApprovals = async () => {
    setApprovals(await listApprovals())
  }

  const refresh = async () => {
    setError(null)
    try {
      const [healthResult, me] = await Promise.all([getHealth(), getMe()])
      setHealth(`${healthResult.status} · ${healthResult.service}`)
      setUserLabel(`${me.displayName} · ${me.authMode}`)
      setSession({
        userId: me.userId,
        organizationId: me.organizationId,
        displayName: me.displayName,
        email: me.email,
        authMode: me.authMode,
      })
      await Promise.all([refreshInbox(), refreshTasks(), refreshAccounts(), refreshApprovals()])
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        setSession(null)
        setUserLabel('Signed out')
        return
      }
      setError(err instanceof Error ? err.message : 'Failed to reach API')
      setHealth('offline')
    }
  }

  const loadMessages = async (conversationId: string) => {
    setMessages(await listMessages(conversationId))
  }

  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    const outlook = params.get('outlook')
    if (outlook === 'connected') {
      setActive('Admin')
      setStatusBanner(
        `Outlook connected${params.get('address') ? `: ${params.get('address')}` : ''}`,
      )
      window.history.replaceState({}, '', '/')
    } else if (outlook === 'error') {
      setActive('Admin')
      const raw = params.get('message') || 'Outlook connection failed'
      setError(decodeURIComponent(raw.replace(/\+/g, ' ')))
      window.history.replaceState({}, '', '/')
    }
    if (getAccessToken()) {
      void refresh()
    } else {
      setHealth('signed out')
      setUserLabel('Signed out')
    }
  }, [])

  useEffect(() => {
    if (!selectedId || !session) {
      setMessages([])
      return
    }
    void loadMessages(selectedId).catch((err) =>
      setError(err instanceof Error ? err.message : 'Could not load messages'),
    )
  }, [selectedId, session])

  const onLogin = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const result = await login(loginEmail.trim(), loginPassword)
      setSession({
        userId: result.userId,
        organizationId: result.organizationId,
        displayName: result.displayName,
        email: result.email,
        authMode: result.authMode,
      })
      setStatusBanner(`Signed in as ${result.displayName}`)
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Sign in failed')
    } finally {
      setBusy(false)
    }
  }

  const onRegister = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const result = await registerPilotUser(
        registerEmail.trim(),
        registerPassword,
        registerName.trim(),
      )
      setSession({
        userId: result.userId,
        organizationId: result.organizationId,
        displayName: result.displayName,
        email: result.email,
        authMode: result.authMode,
      })
      setStatusBanner(`Account created — signed in as ${result.displayName}`)
      setShowRegister(false)
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not create account')
    } finally {
      setBusy(false)
    }
  }

  const onLogout = () => {
    logout()
    clearSession()
    setSession(null)
    setConversations([])
    setTasks([])
    setAccounts([])
    setApprovals([])
    setMessages([])
    setSelectedId(null)
    setUserLabel('Signed out')
    setHealth('signed out')
    setStatusBanner('Signed out')
  }
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
      if (selected?.channel === 'Email' && !asNote) {
        const draft = await createReplyForApproval(selectedId, messageBody.trim())
        setMessageBody('')
        setStatusBanner(
          `Reply queued for approval → ${draft.toAddress} (${draft.subject}). Open Approvals to send.`,
        )
        await refreshApprovals()
        setActive('Approvals')
      } else {
        await addMessage(selectedId, messageBody.trim(), { isInternalNote: asNote })
        setMessageBody('')
        setAsNote(false)
        await Promise.all([loadMessages(selectedId), refreshInbox()])
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not send message')
    } finally {
      setBusy(false)
    }
  }

  const onApprove = async (approvalId: string) => {
    setBusy(true)
    setError(null)
    try {
      const result = await approveRequest(approvalId)
      setStatusBanner(`Approved and sent to ${result.toAddress}`)
      await Promise.all([refreshApprovals(), refreshInbox()])
      if (result.conversationId) {
        setSelectedId(result.conversationId)
        await loadMessages(result.conversationId)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Approve/send failed')
    } finally {
      setBusy(false)
    }
  }

  const onReject = async (approvalId: string) => {
    setBusy(true)
    setError(null)
    try {
      await rejectRequest(approvalId)
      setStatusBanner('Approval rejected')
      await refreshApprovals()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Reject failed')
    } finally {
      setBusy(false)
    }
  }

  const onSummarize = async () => {
    if (!selectedId) return
    setBusy(true)
    setError(null)
    try {
      await summarizeConversation(selectedId)
      setStatusBanner('AI summary saved as an internal note (Ollama).')
      await Promise.all([loadMessages(selectedId), refreshInbox()])
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Summarize failed')
    } finally {
      setBusy(false)
    }
  }

  const onAiDraft = async () => {
    if (!selectedId) return
    setBusy(true)
    setError(null)
    try {
      const draft = await draftReplyWithAi(selectedId, messageBody.trim() || undefined)
      setMessageBody('')
      setStatusBanner(
        `AI draft queued for approval → ${draft.toAddress}. Review in Approvals before send.`,
      )
      await refreshApprovals()
      setActive('Approvals')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'AI draft failed')
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

  const onConnectOutlook = async () => {
    setBusy(true)
    setError(null)
    try {
      const result = await beginMicrosoftAuthorize()
      window.location.href = result.authorizationUrl
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not start Outlook connection')
      setBusy(false)
    }
  }

  const onSyncOutlook = async () => {
    const account = accounts.find(
      (a) => a.connectionStatus === 'Connected' || a.connectionStatus === '1',
    )
    if (!account) {
      setError('Connect Outlook in Admin before syncing.')
      setActive('Admin')
      return
    }

    setBusy(true)
    setError(null)
    try {
      const result = await syncOutlookInbox(account.id)
      setStatusBanner(
        `Synced Outlook: ${result.imported} imported, ${result.skipped} already present (${result.fetched} fetched)`,
      )
      const inbox = await refreshInbox()
      if (result.conversationIds[0]) {
        setSelectedId(result.conversationIds[0])
      } else if (inbox[0]) {
        setSelectedId(inbox[0].id)
      }
      setActive('Inbox')
      await refreshAccounts()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Outlook sync failed')
    } finally {
      setBusy(false)
    }
  }

  const onDisconnect = async (accountId: string) => {
    setBusy(true)
    setError(null)
    try {
      await disconnectAccount(accountId)
      await refreshAccounts()
      setStatusBanner('Outlook disconnected')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not disconnect Outlook')
    } finally {
      setBusy(false)
    }
  }

  const connectedOutlook = accounts.find(
    (a) => a.connectionStatus === 'Connected' || a.connectionStatus === '1',
  )
  const canSendMail =
    !!connectedOutlook?.grantedScopesJson &&
    connectedOutlook.grantedScopesJson.toLowerCase().includes('mail.send')
  const pendingApprovals = approvals.filter((a) => a.status === 'Pending' || a.status === '0')

  const title =
    active === 'Inbox'
      ? 'Unified inbox'
      : active === 'Tasks'
        ? 'Tasks & reminders'
        : active === 'Approvals'
          ? 'Approvals'
          : active === 'Admin'
            ? 'Connectors & admin'
            : active

  if (!session) {
    return (
      <div className="login-shell">
        <form className="login-card" onSubmit={showRegister ? onRegister : onLogin}>
          <div className="brand">
            <span className="brand-mark">P</span>
            <div>
              <strong>Palantir</strong>
              <p>Sable pilot sign-in</p>
            </div>
          </div>
          {showRegister && (
            <label>
              Display name
              <input
                value={registerName}
                onChange={(e) => setRegisterName(e.target.value)}
                autoComplete="name"
                required
              />
            </label>
          )}
          <label>
            Email
            <input
              type="email"
              value={showRegister ? registerEmail : loginEmail}
              onChange={(e) =>
                showRegister ? setRegisterEmail(e.target.value) : setLoginEmail(e.target.value)
              }
              autoComplete="username"
              required
            />
          </label>
          <label>
            Password
            <input
              type="password"
              value={showRegister ? registerPassword : loginPassword}
              onChange={(e) =>
                showRegister
                  ? setRegisterPassword(e.target.value)
                  : setLoginPassword(e.target.value)
              }
              autoComplete={showRegister ? 'new-password' : 'current-password'}
              required
              minLength={showRegister ? 8 : undefined}
            />
          </label>
          {error && <p className="error">{error}</p>}
          <button type="submit" disabled={busy}>
            {busy
              ? showRegister
                ? 'Creating…'
                : 'Signing in…'
              : showRegister
                ? 'Create account'
                : 'Sign in'}
          </button>
          <button
            type="button"
            className="ghost"
            disabled={busy}
            onClick={() => {
              setShowRegister((v) => !v)
              setError(null)
            }}
          >
            {showRegister ? 'Back to sign in' : 'Create another pilot user'}
          </button>
          <p className="muted login-hint">
            Your account: <code>alec.anthony@dnow.com</code> / <code>pilot-demo</code>
            <br />
            Second user for claim/assign demos: <code>demo@palantir.local</code> /{' '}
            <code>pilot-demo</code>
          </p>
        </form>
      </div>
    )
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
          {navItems.map((item) => {
            const soon = !isReadyNav(item)
            const approvalCount =
              item === 'Approvals' && pendingApprovals.length > 0
                ? pendingApprovals.length
                : 0
            return (
              <button
                key={item}
                type="button"
                className={[
                  'nav',
                  item === active ? 'active' : '',
                  soon ? 'soon' : '',
                ]
                  .filter(Boolean)
                  .join(' ')}
                onClick={() => setActive(item)}
              >
                <span>{item}</span>
                {approvalCount > 0 && (
                  <span className="nav-badge" aria-label={`${approvalCount} pending`}>
                    {approvalCount}
                  </span>
                )}
                {soon && <span className="nav-soon">Soon</span>}
              </button>
            )
          })}
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
            <span className={connectedOutlook ? 'pill ok' : 'pill'}>
              {connectedOutlook
                ? `Outlook · ${connectedOutlook.primaryAddress ?? 'connected'}`
                : 'Outlook · not connected'}
            </span>
            <span className={health.startsWith('ok') ? 'pill ok' : 'pill'}>{health}</span>
            <button type="button" className="sign-out" onClick={onLogout}>
              Sign out
            </button>
          </div>
        </header>

        {error && <p className="error">{error}</p>}
        {statusBanner && <p className="banner">{statusBanner}</p>}

        {active === 'Inbox' && (
          <section className="inbox-layout">
            <div className="inbox-list">
              <div className="inbox-toolbar">
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
                {connectedOutlook && (
                  <button
                    type="button"
                    className="sync-btn"
                    onClick={() => void onSyncOutlook()}
                    disabled={busy}
                  >
                    {busy ? 'Syncing…' : 'Sync Outlook'}
                  </button>
                )}
              </div>

              <div className="list">
                {conversations.length === 0 ? (
                  <div className="empty">
                    <h2>Inbox is empty</h2>
                    <p>
                      {connectedOutlook
                        ? 'Click Sync Outlook above to pull in pilot mail, or start a local conversation.'
                        : 'Connect Outlook in Admin, then Sync Outlook here to load pilot mail.'}
                    </p>
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
                          {item.channel} · {item.status} · {assigneeLabel(item, currentUserId)}
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
                      <p>{assigneeLabel(selected, currentUserId)}</p>
                    </div>
                    <div className="actions">
                      <button type="button" className="ghost" onClick={() => void onSummarize()} disabled={busy}>
                        Summarize
                      </button>
                      {selected.channel === 'Email' && (
                        <button
                          type="button"
                          className="ghost"
                          onClick={() => void onAiDraft()}
                          disabled={busy || !canSendMail}
                          title={
                            canSendMail
                              ? 'Draft with local Ollama, then approve before send'
                              : 'Connect Outlook with Mail.Send first'
                          }
                        >
                          AI draft
                        </button>
                      )}
                      {!selected.assignedUserId || selected.assignedUserId !== currentUserId ? (
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
                            <span>
                              {msg.summary === 'AI summary'
                                ? 'AI summary'
                                : msg.isInternalNote
                                  ? 'Internal note'
                                  : msg.direction}
                            </span>
                            <time dateTime={msg.createdAt}>
                              {new Date(msg.createdAt).toLocaleString()}
                            </time>
                          </header>
                          <p style={{ whiteSpace: 'pre-wrap' }}>{msg.body}</p>
                        </article>
                      ))
                    )}
                  </div>

                  <form className="composer thread-composer" onSubmit={onSendMessage}>
                    <input
                      value={messageBody}
                      onChange={(e) => setMessageBody(e.target.value)}
                      placeholder={
                        asNote
                          ? 'Add an internal note…'
                          : selected.channel === 'Email'
                            ? 'Write an Outlook reply (requires approval)…'
                            : 'Write a reply…'
                      }
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
                      {selected.channel === 'Email' && !asNote ? 'Request send' : 'Send'}
                    </button>
                  </form>
                  {selected.channel === 'Email' && !canSendMail && (
                    <p className="muted" style={{ marginTop: '0.5rem' }}>
                      Mail.Send not granted yet — reconnect Outlook in Admin after Azure has Mail.Send.
                    </p>
                  )}
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

        {active === 'Approvals' && (
          <section className="tasks">
            {!canSendMail && connectedOutlook && (
              <p className="error">
                Reconnect Outlook in Admin to grant Mail.Send before approving outbound mail.
              </p>
            )}
            <div className="list">
              {pendingApprovals.length === 0 ? (
                <div className="empty">
                  <h2>No pending approvals</h2>
                  <p>
                    Open an Email thread in Inbox, write a reply, then click Request send — drafts
                    land here for Approve &amp; send.
                  </p>
                </div>
              ) : (
                pendingApprovals.map((item) => (
                  <article key={item.id} className="row task-row">
                    <div>
                      <h3>{item.draftSubject || 'Outbound reply'}</h3>
                      <p>
                        To {item.draftTo || 'unknown'} · {item.status}
                      </p>
                      <p style={{ marginTop: '0.5rem', whiteSpace: 'pre-wrap' }}>{item.draftBody}</p>
                    </div>
                    <div className="actions">
                      <button type="button" onClick={() => void onApprove(item.id)} disabled={busy}>
                        Approve & send
                      </button>
                      <button
                        type="button"
                        className="ghost"
                        onClick={() => void onReject(item.id)}
                        disabled={busy}
                      >
                        Reject
                      </button>
                    </div>
                  </article>
                ))
              )}
            </div>
          </section>
        )}

        {active === 'Admin' && (
          <section className="admin">
            <div className="panel">
              <h2>Connect Outlook</h2>
              <p>
                Demo path: Connect the pilot mailbox → Sync into Inbox → reply from an Email
                thread → approve in Approvals.
              </p>
              <p className="muted" style={{ marginTop: '0.5rem' }}>
                Pilot mailbox: <code>palantir.pilot.aanthony@outlook.com</code>
              </p>
              <p className="muted" style={{ marginTop: '0.5rem' }}>
                {canSendMail
                  ? 'Mail.Read + Mail.Send granted — ready to sync and send.'
                  : connectedOutlook
                    ? 'Connected for read. After adding Mail.Send in Azure, disconnect and connect again to consent send.'
                    : 'Not connected yet — use Connect Outlook to authorize Graph access.'}
              </p>
              <div className="actions" style={{ marginTop: '1rem' }}>
                <button type="button" onClick={() => void onConnectOutlook()} disabled={busy}>
                  {busy ? 'Redirecting…' : connectedOutlook ? 'Reconnect Outlook' : 'Connect Outlook'}
                </button>
                {accounts.some(
                  (a) => a.connectionStatus === 'Connected' || a.connectionStatus === '1',
                ) && (
                  <button type="button" className="ghost" onClick={() => void onSyncOutlook()} disabled={busy}>
                    Sync into Inbox
                  </button>
                )}
              </div>
            </div>

            <div className="list" style={{ marginTop: '1rem' }}>
              {accounts.length === 0 ? (
                <div className="empty">
                  <h2>No connected accounts</h2>
                  <p>Connect the pilot Outlook mailbox to read recent mail via Microsoft Graph.</p>
                </div>
              ) : (
                accounts.map((account) => (
                  <article key={account.id} className="row task-row">
                    <div>
                      <h3>{account.primaryAddress || account.displayName || account.id}</h3>
                      <p>
                        {account.provider} · {account.connectionStatus}
                        {account.lastSuccessfulSyncAt
                          ? ` · synced ${new Date(account.lastSuccessfulSyncAt).toLocaleString()}`
                          : ''}
                      </p>
                    </div>
                    {account.connectionStatus !== 'Revoked' && (
                      <button
                        type="button"
                        className="ghost"
                        onClick={() => void onDisconnect(account.id)}
                        disabled={busy}
                      >
                        Disconnect
                      </button>
                    )}
                  </article>
                ))
              )}
            </div>

            {outlookMail.length > 0 && (
              <div className="list" style={{ marginTop: '1rem' }}>
                <h2 style={{ margin: '0 0 0.5rem', fontSize: '1rem' }}>Recent Outlook mail</h2>
                {outlookMail.map((mail) => (
                  <article key={mail.id} className="row">
                    <div>
                      <h3>{mail.subject || '(no subject)'}</h3>
                      <p>
                        {mail.from || 'unknown'} · {mail.preview}
                      </p>
                    </div>
                    {mail.receivedAt && (
                      <time dateTime={mail.receivedAt}>
                        {new Date(mail.receivedAt).toLocaleString()}
                      </time>
                    )}
                  </article>
                ))}
              </div>
            )}
          </section>
        )}

        {!isReadyNav(active) && (
          <section className="panel placeholder">
            <h2>{active}</h2>
            <p>
              Not in this pilot demo. Use Inbox, Admin (connect &amp; sync), then Approvals for the
              walkthrough.
            </p>
          </section>
        )}
      </main>
    </div>
  )
}
