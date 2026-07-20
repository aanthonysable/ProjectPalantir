import { FormEvent, useEffect, useRef, useState } from 'react'
import { MenuSelect } from './MenuSelect'
import {
  ApiError,
  ApprovalItem,
  AuthProviders,
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
  exchangeEntraToken,
  getAccessToken,
  getAuthProviders,
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
  defaultOverviewFocus,
  askOverviewChat,
  uploadAskAttachments,
  getAskAttachments,
  listAskSessions,
  getAskSession,
  deleteAskSession,
  getOpsHealth,
  getOpsOpenWork,
  getWhatsAppStatus,
  getWhatsAppGaps,
  proposeOpsWriteBack,
  getAiStatus,
  getKnowledgeStatus,
  listKnowledgeDocuments,
  getKnowledgeLibrary,
  uploadKnowledgeDocuments,
  deleteKnowledgeDocument,
  downloadKnowledgeFile,
  fetchKnowledgeFileBlob,
  proposeKnowledgeCapture,
  AiStatus,
  ConnectorHealth,
  ExternalWorkItem,
  KnowledgeDocument,
  KnowledgeLibrary,
  KnowledgeSource,
  KnowledgeStatus,
  UploadProgress,
  OverviewChatTurn,
  OverviewFocus,
  AskSessionSummary,
  WhatsAppBridgeStatus,
  WhatsAppGap,
} from './api'
import { signInWithEntra } from './entraAuth'
import { BrandMark } from './BrandMark'
import {
  DEFAULT_THEME,
  THEME_PRESETS,
  ThemeColors,
  isHexColor,
  loadTheme,
  persistAndApplyTheme,
} from './theme'
import './App.css'

const readyNavItems = ['Ask', 'Knowledge', 'Open work', 'Inbox', 'Tasks', 'Approvals', 'Admin'] as const
const soonNavItems = ['Projects', 'Customers'] as const
const navItems = [...readyNavItems, ...soonNavItems] as const
type NavItem = (typeof navItems)[number]

const OVERVIEW_PREFS_KEY = 'palantir.overviewFocus.v4'

const KNOWLEDGE_SOURCES_RE =
  /‹knowledge-sources›\s*([\s\S]*?)‹\/knowledge-sources›/i

function parseKnowledgeSourcesFromContent(content: string): {
  display: string
  sources: KnowledgeSource[]
} {
  const match = KNOWLEDGE_SOURCES_RE.exec(content)
  if (!match) {
    return { display: content, sources: [] }
  }

  const sources: KnowledgeSource[] = []
  for (const line of match[1].split('\n')) {
    const trimmed = line.trim()
    if (!trimmed) continue
    const parts = trimmed.split('|')
    if (parts.length < 2) continue
    const [documentId, title, fileName] = parts
    if (!documentId) continue
    sources.push({
      documentId: documentId.trim(),
      title: (title || 'Knowledge document').trim(),
      fileName: (fileName || title || 'file').trim(),
    })
  }

  const display = content.replace(KNOWLEDGE_SOURCES_RE, '').trimEnd()
  return { display, sources }
}

type AskUploadChip = {
  localKey: string
  fileName: string
  id: string | null
  status: string
  error?: string
}

function askExtractPending(status: string) {
  return status === 'uploading' || status === 'Queued' || status === 'Extracting'
}

type OpenWorkFilters = {
  source: string
  environment: string
  overdueOnly: boolean
  physicalOnly: boolean
  query: string
}

const defaultOpenWorkFilters = (): OpenWorkFilters => ({
  source: 'all',
  environment: 'all',
  overdueOnly: false,
  physicalOnly: true,
  query: '',
})

function isOverdue(item: ExternalWorkItem, now = Date.now()) {
  if (!item.dueAt) return false
  const due = new Date(item.dueAt).getTime()
  return !Number.isNaN(due) && due < now
}

function isPhysicalMaintainX(item: ExternalWorkItem) {
  if (item.sourceSystem !== 'MaintainX') return true
  const raw = (item.metadata?.rawStatus || '').toUpperCase().replace(/\s+/g, '_')
  return raw === 'OPEN' || raw === 'IN_PROGRESS'
}

function sourceBadgeClass(source: string) {
  const key = source.trim().toLowerCase()
  if (key.includes('maintain')) return 'source-badge mx'
  if (key.includes('ezrent') || key.includes('ez rent')) return 'source-badge ez'
  if (key.includes('monday')) return 'source-badge monday'
  return 'source-badge'
}

function loadOverviewFocus(): OverviewFocus {
  const stored = localStorage.getItem(OVERVIEW_PREFS_KEY)
  if (!stored) return defaultOverviewFocus()
  try {
    return { ...defaultOverviewFocus(), ...(JSON.parse(stored) as Partial<OverviewFocus>) }
  } catch {
    return defaultOverviewFocus()
  }
}

function saveOverviewFocus(focus: OverviewFocus) {
  localStorage.setItem(OVERVIEW_PREFS_KEY, JSON.stringify(focus))
}

function isReadyNav(item: NavItem): item is (typeof readyNavItems)[number] {
  return (readyNavItems as readonly string[]).includes(item)
}

function assigneeLabel(conversation: Conversation, currentUserId: string | null) {
  if (!conversation.assignedUserId) return 'Unassigned'
  if (currentUserId && conversation.assignedUserId === currentUserId) return 'Assigned to you'
  return `Assigned · ${conversation.assignedUserId.slice(0, 8)}`
}

function formatBytes(bytes: number) {
  if (!Number.isFinite(bytes) || bytes < 0) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`
}

function formatEta(seconds: number | null) {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return null
  if (seconds < 5) return 'a few seconds'
  if (seconds < 60) return `~${Math.ceil(seconds)}s left`
  const mins = Math.floor(seconds / 60)
  const secs = Math.ceil(seconds % 60)
  if (mins < 60) return secs > 0 ? `~${mins}m ${secs}s left` : `~${mins}m left`
  const hours = Math.floor(mins / 60)
  const remMins = mins % 60
  return remMins > 0 ? `~${hours}h ${remMins}m left` : `~${hours}h left`
}

function PanelWorking({ label }: { label: string }) {
  return (
    <div className="ask-thinking panel-working" role="status" aria-live="polite">
      <span className="ask-thinking-label">
        {label}
        <span className="ask-thinking-dots" aria-hidden="true">
          <span />
          <span />
          <span />
        </span>
      </span>
      <span className="ask-thinking-bar" aria-hidden="true" />
    </div>
  )
}

export default function App() {
  const [session, setSession] = useState<SessionUser | null>(() => getStoredSession())
  const [loginEmail, setLoginEmail] = useState('alec.anthony@dnow.com')
  const [loginPassword, setLoginPassword] = useState('pilot-demo')
  const [showRegister, setShowRegister] = useState(false)
  const [registerName, setRegisterName] = useState('')
  const [registerEmail, setRegisterEmail] = useState('')
  const [registerPassword, setRegisterPassword] = useState('')
  const [authProviders, setAuthProviders] = useState<AuthProviders | null>(null)
  const [active, setActive] = useState<NavItem>('Inbox')
  const [navOpen, setNavOpen] = useState(false)
  const [askHistoryOpen, setAskHistoryOpen] = useState(false)
  const [inboxComposeOpen, setInboxComposeOpen] = useState(false)
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
  const [threadSortNewestFirst, setThreadSortNewestFirst] = useState(() => {
    const stored = localStorage.getItem('palantir.threadSort')
    return stored !== 'oldest'
  })
  const [overviewFocus, setOverviewFocus] = useState<OverviewFocus>(() => loadOverviewFocus())
  const [overviewChat, setOverviewChat] = useState<OverviewChatTurn[]>([])
  const [overviewChatDraft, setOverviewChatDraft] = useState('')
  const [overviewChatBusy, setOverviewChatBusy] = useState(false)
  const [askThinkingSeconds, setAskThinkingSeconds] = useState(0)
  const [askSourcesOpen, setAskSourcesOpen] = useState(false)
  const [askSessions, setAskSessions] = useState<AskSessionSummary[]>([])
  const [askSessionId, setAskSessionId] = useState<string | null>(null)
  const [askPendingFiles, setAskPendingFiles] = useState<AskUploadChip[]>([])
  const askFileInputRef = useRef<HTMLInputElement | null>(null)
  const textPromptInputRef = useRef<HTMLInputElement | HTMLTextAreaElement | null>(null)
  const textPromptResolver = useRef<((value: string | null) => void) | null>(null)
  const [textPrompt, setTextPrompt] = useState<{
    heading: string
    description?: string
    label?: string
    confirmLabel: string
    multiline: boolean
    value: string
  } | null>(null)
  const [opsHealth, setOpsHealth] = useState<ConnectorHealth[]>([])
  const [whatsAppStatus, setWhatsAppStatus] = useState<WhatsAppBridgeStatus | null>(null)
  const [whatsAppGaps, setWhatsAppGaps] = useState<WhatsAppGap[]>([])
  const [aiStatus, setAiStatus] = useState<AiStatus | null>(null)
  const [knowledgeStatus, setKnowledgeStatus] = useState<KnowledgeStatus | null>(null)
  const [knowledgeDocs, setKnowledgeDocs] = useState<KnowledgeDocument[]>([])
  const [knowledgeLibrary, setKnowledgeLibrary] = useState<KnowledgeLibrary | null>(null)
  const [knowledgeBrowseCollection, setKnowledgeBrowseCollection] = useState<string>('all')
  const [knowledgeBrowseFolder, setKnowledgeBrowseFolder] = useState<string>('all')
  const [knowledgeBrowseQuery, setKnowledgeBrowseQuery] = useState('')
  const [knowledgeTitle, setKnowledgeTitle] = useState('')
  const [knowledgeUploadOpen, setKnowledgeUploadOpen] = useState(false)
  const [knowledgePreview, setKnowledgePreview] = useState<{
    documentId: string
    title: string
    fileName: string
    loading: boolean
    error: string | null
    objectUrl: string | null
    contentType: string | null
    textPreview: string | null
  } | null>(null)
  const [uploadProgress, setUploadProgress] = useState<{
    percent: number
    loaded: number
    total: number
    processing: boolean
    fileLabel: string
    etaSeconds: number | null
  } | null>(null)
  const [openWorkItems, setOpenWorkItems] = useState<ExternalWorkItem[]>([])
  const [openWorkFilters, setOpenWorkFilters] = useState<OpenWorkFilters>(() =>
    defaultOpenWorkFilters(),
  )
  const [openWorkLoadedAt, setOpenWorkLoadedAt] = useState<string | null>(null)
  const [openWorkLoading, setOpenWorkLoading] = useState(false)
  const [openWorkReady, setOpenWorkReady] = useState(false)
  const [knowledgeLoading, setKnowledgeLoading] = useState(false)
  const [knowledgeReady, setKnowledgeReady] = useState(false)
  const [writeBackTarget, setWriteBackTarget] = useState<ExternalWorkItem | null>(null)
  const [writeBackBody, setWriteBackBody] = useState('')
  const [theme, setTheme] = useState<ThemeColors>(() => loadTheme())
  const askThreadRef = useRef<HTMLDivElement>(null)

  const selected = conversations.find((c) => c.id === selectedId) ?? null
  const currentUserId = session?.userId ?? null
  const sortedMessages = [...messages].sort((a, b) => {
    const delta = new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()
    return threadSortNewestFirst ? -delta : delta
  })

  const setThreadSort = (newestFirst: boolean) => {
    setThreadSortNewestFirst(newestFirst)
    localStorage.setItem('palantir.threadSort', newestFirst ? 'newest' : 'oldest')
  }

  const updateOverviewFocus = (patch: Partial<OverviewFocus>) => {
    setOverviewFocus((prev) => {
      const next = { ...prev, ...patch }
      saveOverviewFocus(next)
      return next
    })
  }

  const onOverviewChatSend = async (question?: string) => {
    const text = (question ?? overviewChatDraft).trim()
    const pendingFiles = askPendingFiles
    if ((!text && pendingFiles.length === 0) || overviewChatBusy) return
    if (pendingFiles.some((f) => f.status === 'uploading' || f.status === 'error' || !f.id)) {
      setError('Wait for attachments to finish uploading before asking.')
      return
    }

    const content =
      text ||
      (pendingFiles.length === 1
        ? `Please review the attached file (${pendingFiles[0].fileName}).`
        : `Please review the ${pendingFiles.length} attached files.`)
    const fileNote =
      pendingFiles.length > 0
        ? `\n\n[Attached: ${pendingFiles.map((f) => f.fileName).join(', ')}]`
        : ''
    const nextTurns: OverviewChatTurn[] = [
      ...overviewChat,
      { role: 'user', content: content + fileNote },
    ]
    setOverviewChat(nextTurns)
    setOverviewChatDraft('')
    setAskPendingFiles([])
    setOverviewChatBusy(true)
    setError(null)
    try {
      const attachmentIds = pendingFiles.map((f) => f.id!).filter(Boolean)
      const reply = await askOverviewChat(
        overviewFocus,
        nextTurns,
        false,
        askSessionId,
        attachmentIds,
      )
      const parsedReply = parseKnowledgeSourcesFromContent(reply.reply)
      setOverviewChat([
        ...nextTurns,
        {
          role: 'assistant',
          content: parsedReply.display,
          knowledgeSources:
            reply.knowledgeSources && reply.knowledgeSources.length > 0
              ? reply.knowledgeSources
              : parsedReply.sources.length > 0
                ? parsedReply.sources
                : undefined,
        },
      ])
      setAskSessionId(reply.sessionId)
      setStatusBanner(`Live facts · ${new Date(reply.generatedAt).toLocaleString()}`)
      const sessions = await listAskSessions()
      setAskSessions(sessions)
    } catch (err) {
      setOverviewChat(overviewChat)
      setOverviewChatDraft(text)
      setAskPendingFiles(pendingFiles)
      setError(err instanceof Error ? err.message : 'Chat failed')
    } finally {
      setOverviewChatBusy(false)
    }
  }

  const onAskFilesChosen = (files: FileList | null) => {
    if (!files || files.length === 0) return
    const room = Math.max(0, 5 - askPendingFiles.length)
    const chosen = Array.from(files).slice(0, room)
    if (chosen.length === 0) return

    const chips: AskUploadChip[] = chosen.map((file) => ({
      localKey: `${file.name}-${file.size}-${file.lastModified}-${Math.random().toString(36).slice(2, 8)}`,
      fileName: file.name,
      id: null,
      status: 'uploading',
    }))
    setAskPendingFiles((prev) => [...prev, ...chips].slice(0, 5))
    setError(null)

    void (async () => {
      try {
        const uploaded = await uploadAskAttachments(chosen, askSessionId)
        setAskPendingFiles((prev) => {
          const next = [...prev]
          for (let i = 0; i < chips.length; i++) {
            const chipIdx = next.findIndex((c) => c.localKey === chips[i].localKey)
            const row = uploaded[i]
            if (chipIdx < 0) continue
            if (!row) {
              next[chipIdx] = {
                ...next[chipIdx],
                status: 'error',
                error: 'Upload returned no attachment',
              }
              continue
            }
            next[chipIdx] = {
              ...next[chipIdx],
              id: row.id,
              status: row.extractStatus || 'Queued',
            }
          }
          return next
        })
        setStatusBanner(
          uploaded.length === 1
            ? `Attached ${uploaded[0].fileName} — extracting in background…`
            : `Attached ${uploaded.length} files — extracting in background…`,
        )
      } catch (err) {
        setAskPendingFiles((prev) =>
          prev.map((c) =>
            chips.some((x) => x.localKey === c.localKey)
              ? {
                  ...c,
                  status: 'error',
                  error: err instanceof Error ? err.message : 'Upload failed',
                }
              : c,
          ),
        )
        setError(err instanceof Error ? err.message : 'Attachment upload failed')
      } finally {
        if (askFileInputRef.current) askFileInputRef.current.value = ''
      }
    })()
  }

  useEffect(() => {
    const pendingIds = askPendingFiles
      .filter((f) => f.id && (f.status === 'Queued' || f.status === 'Extracting'))
      .map((f) => f.id!)
    if (pendingIds.length === 0) return

    const timer = window.setInterval(() => {
      void getAskAttachments(pendingIds)
        .then((rows) => {
          setAskPendingFiles((prev) =>
            prev.map((chip) => {
              if (!chip.id) return chip
              const row = rows.find((r) => r.id === chip.id)
              if (!row) return chip
              return { ...chip, status: row.extractStatus || chip.status }
            }),
          )
        })
        .catch(() => {
          /* ignore transient poll errors */
        })
    }, 1500)

    return () => window.clearInterval(timer)
  }, [
    askPendingFiles
      .filter((f) => f.status === 'Queued' || f.status === 'Extracting')
      .map((f) => f.id)
      .join(','),
  ])

  const refreshAskSessions = async () => {
    setAskSessions(await listAskSessions())
  }

  const onNewAskChat = () => {
    setAskSessionId(null)
    setOverviewChat([])
    setOverviewChatDraft('')
    setAskPendingFiles([])
    setError(null)
  }

  const onSelectAskSession = async (sessionId: string) => {
    setBusy(true)
    setError(null)
    try {
      const detail = await getAskSession(sessionId)
      setAskSessionId(detail.id)
      setOverviewChat(
        detail.messages.map((m) => {
          const role = m.role === 'assistant' ? 'assistant' : 'user'
          if (role !== 'assistant') {
            return { role, content: m.content }
          }
          const parsed = parseKnowledgeSourcesFromContent(m.content)
          return {
            role,
            content: parsed.display,
            knowledgeSources: parsed.sources.length > 0 ? parsed.sources : undefined,
          }
        }),
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load chat')
    } finally {
      setBusy(false)
    }
  }

  const onDeleteAskSession = async (sessionId: string) => {
    setBusy(true)
    setError(null)
    try {
      await deleteAskSession(sessionId)
      if (askSessionId === sessionId) {
        onNewAskChat()
      }
      await refreshAskSessions()
      setStatusBanner('Chat deleted')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not delete chat')
    } finally {
      setBusy(false)
    }
  }
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

  const refreshOpsHealth = async () => {
    setOpsHealth(await getOpsHealth())
  }

  const refreshWhatsApp = async () => {
    const [status, gaps] = await Promise.all([
      getWhatsAppStatus(),
      getWhatsAppGaps().catch(() => [] as WhatsAppGap[]),
    ])
    setWhatsAppStatus(status)
    setWhatsAppGaps(gaps)
  }

  const refreshAiStatus = async () => {
    setAiStatus(await getAiStatus())
  }

  const refreshKnowledge = async () => {
    setKnowledgeLoading(true)
    try {
      const [status, docs, library] = await Promise.all([
        getKnowledgeStatus(),
        listKnowledgeDocuments(),
        getKnowledgeLibrary().catch(() => null),
      ])
      setKnowledgeStatus(status)
      setKnowledgeDocs(docs)
      setKnowledgeLibrary(library)
      setKnowledgeReady(true)
    } finally {
      setKnowledgeLoading(false)
    }
  }

  const closeKnowledgePreview = () => {
    setKnowledgePreview((prev) => {
      if (prev?.objectUrl) URL.revokeObjectURL(prev.objectUrl)
      return null
    })
  }

  const openKnowledgePreview = (doc: {
    documentId: string
    title: string
    fileName: string
  }) => {
    setKnowledgePreview((prev) => {
      if (prev?.objectUrl) URL.revokeObjectURL(prev.objectUrl)
      return {
        documentId: doc.documentId,
        title: doc.title,
        fileName: doc.fileName,
        loading: true,
        error: null,
        objectUrl: null,
        contentType: null,
        textPreview: null,
      }
    })
    void fetchKnowledgeFileBlob(doc.documentId, doc.fileName)
      .then(async (file) => {
        const type = (file.contentType || '').toLowerCase()
        const name = file.fileName.toLowerCase()
        const isText =
          type.startsWith('text/') ||
          type.includes('json') ||
          type.includes('xml') ||
          type.includes('markdown') ||
          /\.(txt|md|csv|json|log|xml|html|htm)$/i.test(name)

        let textPreview: string | null = null
        if (isText && file.blob.size <= 2 * 1024 * 1024) {
          textPreview = await file.blob.text()
        }

        setKnowledgePreview((prev) => {
          if (!prev || prev.documentId !== doc.documentId) {
            URL.revokeObjectURL(file.objectUrl)
            return prev
          }
          return {
            ...prev,
            loading: false,
            objectUrl: file.objectUrl,
            contentType: file.contentType,
            fileName: file.fileName,
            textPreview,
          }
        })
      })
      .catch((err) => {
        setKnowledgePreview((prev) =>
          prev && prev.documentId === doc.documentId
            ? {
                ...prev,
                loading: false,
                error: err instanceof Error ? err.message : 'Could not open preview',
              }
            : prev,
        )
      })
  }

  const onUploadKnowledge = async (fileList: FileList | File[] | null) => {
    const files = fileList ? Array.from(fileList).filter((f) => f.size > 0) : []
    if (files.length === 0) return
    setBusy(true)
    setError(null)
    const totalBytes = files.reduce((sum, f) => sum + f.size, 0)
    const fileLabel =
      files.length === 1
        ? files[0].name
        : `${files.length} files (${formatBytes(totalBytes)})`
    const startedAt = performance.now()
    setUploadProgress({
      percent: 0,
      loaded: 0,
      total: totalBytes,
      processing: false,
      fileLabel,
      etaSeconds: null,
    })
    try {
      const batch = await uploadKnowledgeDocuments(
        files,
        knowledgeTitle || undefined,
        (progress: UploadProgress) => {
          const elapsedSec = Math.max((performance.now() - startedAt) / 1000, 0.05)
          const speed = progress.loaded / elapsedSec
          const remaining = progress.total - progress.loaded
          const etaSeconds =
            progress.processing || speed <= 0 || remaining <= 0
              ? null
              : remaining / speed
          setUploadProgress({
            percent: progress.percent,
            loaded: progress.loaded,
            total: progress.total || totalBytes,
            processing: progress.processing,
            fileLabel,
            etaSeconds,
          })
        },
      )
      setKnowledgeTitle('')
      setKnowledgeUploadOpen(false)
      const queued = batch.results.filter(
        (r) => r.document.status === 'Queued' || r.document.status === 'Indexing',
      ).length
      const total = batch.results.length
      const note = batch.notes[0]
      setStatusBanner(
        note ||
          (queued > 0
            ? total === 1
              ? `Uploaded “${batch.results[0].document.title}” — indexing in the background`
              : `Uploaded ${total} file(s) — indexing ${queued} in the background`
            : total === 1
              ? `Stored “${batch.results[0].document.title}” · ${batch.results[0].document.status}`
              : `Processed ${total} file(s)` +
                (batch.skippedEntries ? `, skipped ${batch.skippedEntries}` : '')),
      )
      await refreshKnowledge()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Knowledge upload failed')
    } finally {
      setBusy(false)
      setUploadProgress(null)
    }
  }

  const onDeleteKnowledge = async (documentId: string) => {
    setBusy(true)
    setError(null)
    try {
      await deleteKnowledgeDocument(documentId)
      setStatusBanner('Knowledge document deleted')
      await refreshKnowledge()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Delete failed')
    } finally {
      setBusy(false)
    }
  }

  const refreshOpenWork = async () => {
    setOpenWorkLoading(true)
    try {
      const items = await getOpsOpenWork()
      setOpenWorkItems(items)
      setOpenWorkLoadedAt(new Date().toISOString())
      setOpenWorkReady(true)
    } finally {
      setOpenWorkLoading(false)
    }
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
    if (!statusBanner) return
    const timer = window.setTimeout(() => setStatusBanner(null), 4500)
    return () => window.clearTimeout(timer)
  }, [statusBanner])

  useEffect(() => {
    if (!error) return
    const timer = window.setTimeout(() => setError(null), 8000)
    return () => window.clearTimeout(timer)
  }, [error])

  useEffect(() => {
    if (active !== 'Ask') return
    const el = askThreadRef.current
    if (!el) return
    el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' })
  }, [active, overviewChat, overviewChatBusy])

  useEffect(() => {
    if (!overviewChatBusy) {
      setAskThinkingSeconds(0)
      return
    }
    setAskThinkingSeconds(0)
    const started = Date.now()
    const timer = window.setInterval(() => {
      setAskThinkingSeconds(Math.floor((Date.now() - started) / 1000))
    }, 1000)
    return () => window.clearInterval(timer)
  }, [overviewChatBusy])

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
    void getAuthProviders()
      .then(setAuthProviders)
      .catch(() => setAuthProviders(null))
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

  useEffect(() => {
    if (!session) return
    if (active === 'Admin') {
      void Promise.all([refreshOpsHealth(), refreshAiStatus(), refreshWhatsApp()]).catch((err) =>
        setError(err instanceof Error ? err.message : 'Admin status check failed'),
      )
    }
    if (active === 'Knowledge') {
      void refreshKnowledge().catch((err) =>
        setError(err instanceof Error ? err.message : 'Knowledge load failed'),
      )
    }
    if (active === 'Open work') {
      void refreshOpenWork().catch((err) =>
        setError(err instanceof Error ? err.message : 'Open work load failed'),
      )
    }
    if (active === 'Ask') {
      void refreshAskSessions().catch((err) =>
        setError(err instanceof Error ? err.message : 'Could not load chat history'),
      )
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, session?.userId])

  useEffect(() => {
    if (!session) return
    const pending = knowledgeDocs.some(
      (d) => d.status === 'Queued' || d.status === 'Indexing',
    )
    if (!pending) return

    const timer = window.setInterval(() => {
      void refreshKnowledge().catch(() => undefined)
    }, 2500)

    return () => window.clearInterval(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    session?.userId,
    knowledgeDocs.some((d) => d.status === 'Queued' || d.status === 'Indexing'),
  ])

  const onEntraSignIn = async () => {
    const entra = authProviders?.entraExternalId
    if (!entra?.enabled) return
    setBusy(true)
    setError(null)
    try {
      const tokens = await signInWithEntra(entra)
      const result = await exchangeEntraToken(tokens)
      setSession({
        userId: result.userId,
        organizationId: result.organizationId,
        displayName: result.displayName,
        email: result.email,
        authMode: result.authMode,
      })
      setStatusBanner(`Signed in with Microsoft as ${result.displayName}`)
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Microsoft sign-in failed')
    } finally {
      setBusy(false)
    }
  }

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
      const pending = approvals.find((a) => a.id === approvalId)
      const result = await approveRequest(approvalId)
      const isOps =
        pending?.draftKind === 'maintainx.comment' || pending?.draftKind === 'monday.update'
      const isKnowledge = pending?.draftKind === 'knowledge.save'
      setStatusBanner(
        isKnowledge
          ? `Approved and saved to knowledge · ${result.subject}`
          : isOps
            ? `Approved and posted to ${result.toAddress}`
            : `Approved and sent to ${result.toAddress}`,
      )
      await Promise.all([refreshApprovals(), refreshInbox()])
      if (isKnowledge) {
        await refreshKnowledge().catch(() => undefined)
      }
      if (!isOps && !isKnowledge && result.conversationId) {
        setSelectedId(result.conversationId)
        await loadMessages(result.conversationId)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Approve/send failed')
    } finally {
      setBusy(false)
    }
  }

  const closeTextPrompt = (value: string | null) => {
    const resolve = textPromptResolver.current
    textPromptResolver.current = null
    setTextPrompt(null)
    resolve?.(value)
  }

  const promptText = (options: {
    heading: string
    description?: string
    label?: string
    defaultValue?: string
    confirmLabel?: string
    multiline?: boolean
  }) =>
    new Promise<string | null>((resolve) => {
      // Replace any in-flight prompt (should be rare).
      textPromptResolver.current?.(null)
      textPromptResolver.current = resolve
      setTextPrompt({
        heading: options.heading,
        description: options.description,
        label: options.label ?? 'Value',
        confirmLabel: options.confirmLabel ?? 'Save',
        multiline: options.multiline ?? false,
        value: options.defaultValue ?? '',
      })
    })

  useEffect(() => {
    if (!textPrompt) return
    const frame = window.requestAnimationFrame(() => {
      textPromptInputRef.current?.focus()
      textPromptInputRef.current?.select?.()
    })
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        closeTextPrompt(null)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => {
      window.cancelAnimationFrame(frame)
      window.removeEventListener('keydown', onKey)
    }
    // Only when the dialog opens — not on every keystroke (that re-selects and overwrites).
  }, [textPrompt?.heading, textPrompt?.label, textPrompt?.multiline])

  const onSaveAskToKnowledge = async (answer: string, question: string | null) => {
    const titleDefault =
      (question && question.trim().slice(0, 80)) ||
      answer.trim().split('\n')[0]?.slice(0, 80) ||
      'Captured ops knowledge'
    const title = await promptText({
      heading: 'Save to knowledge',
      description:
        'Queues an approval to add this Ask answer to shared org knowledge. Edit the title if you want.',
      label: 'Title',
      defaultValue: titleDefault,
      confirmLabel: 'Queue for approval',
    })
    if (title == null) return
    if (!title.trim() || !answer.trim()) return

    setBusy(true)
    setError(null)
    try {
      const result = await proposeKnowledgeCapture({
        title: title.trim(),
        body: answer.trim(),
        sourceQuestion: question?.trim() || null,
        createdByAi: true,
      })
      setStatusBanner(`Knowledge capture queued · ${result.subject}`)
      await refreshApprovals()
      setActive('Approvals')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not queue knowledge capture')
    } finally {
      setBusy(false)
    }
  }

  const supportsWriteBack = (item: ExternalWorkItem) =>
    item.sourceSystem === 'MaintainX' || item.sourceSystem === 'Monday'

  const onProposeWriteBack = async (event: FormEvent) => {
    event.preventDefault()
    if (!writeBackTarget || !writeBackBody.trim()) return
    setBusy(true)
    setError(null)
    try {
      const result = await proposeOpsWriteBack({
        sourceSystem: writeBackTarget.sourceSystem,
        environmentName: writeBackTarget.environmentName,
        externalId: writeBackTarget.externalId,
        title: writeBackTarget.title,
        body: writeBackBody.trim(),
      })
      setWriteBackTarget(null)
      setWriteBackBody('')
      setStatusBanner(`Write-back queued for approval · ${result.subject}`)
      await refreshApprovals()
      setActive('Approvals')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not queue write-back')
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

  const onRefreshOpsHealth = async () => {
    setBusy(true)
    setError(null)
    try {
      await Promise.all([refreshOpsHealth(), refreshAiStatus(), refreshWhatsApp()])
      setStatusBanner(`Ops + AI + WhatsApp status checked · ${new Date().toLocaleString()}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Status check failed')
    } finally {
      setBusy(false)
    }
  }

  const onRefreshWhatsAppGaps = async () => {
    setBusy(true)
    setError(null)
    try {
      await refreshWhatsApp()
      setStatusBanner(`WhatsApp gaps refreshed · ${new Date().toLocaleString()}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'WhatsApp gaps failed')
    } finally {
      setBusy(false)
    }
  }

  const onRefreshOpenWork = async () => {
    setBusy(true)
    setError(null)
    try {
      await refreshOpenWork()
      setStatusBanner(`Open work refreshed · ${new Date().toLocaleString()}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Open work refresh failed')
    } finally {
      setBusy(false)
    }
  }

  const updateTheme = (patch: Partial<ThemeColors>) => {
    const next = { ...theme, ...patch }
    setTheme(next)
    if (isHexColor(next.primary) && isHexColor(next.secondary)) {
      persistAndApplyTheme(next)
    }
  }

  const applyThemePreset = (colors: ThemeColors) => {
    setTheme(colors)
    persistAndApplyTheme(colors)
    setStatusBanner('Appearance updated')
  }

  const connectedOutlook = accounts.find(
    (a) => a.connectionStatus === 'Connected' || a.connectionStatus === '1',
  )
  const canSendMail =
    !!connectedOutlook?.grantedScopesJson &&
    connectedOutlook.grantedScopesJson.toLowerCase().includes('mail.send')
  const pendingApprovals = approvals.filter((a) => a.status === 'Pending' || a.status === '0')

  const openWorkSources = Array.from(
    new Set(openWorkItems.map((i) => i.sourceSystem).filter(Boolean)),
  ).sort()
  const openWorkEnvironments = Array.from(
    new Set(
      openWorkItems
        .map((i) => i.environmentName)
        .filter((name): name is string => !!name && name.trim().length > 0),
    ),
  ).sort()
  const filteredOpenWork = openWorkItems.filter((item) => {
    if (openWorkFilters.source !== 'all' && item.sourceSystem !== openWorkFilters.source) {
      return false
    }
    if (
      openWorkFilters.environment !== 'all' &&
      (item.environmentName ?? '') !== openWorkFilters.environment
    ) {
      return false
    }
    if (openWorkFilters.overdueOnly && !isOverdue(item)) return false
    if (openWorkFilters.physicalOnly && !isPhysicalMaintainX(item)) return false
    const q = openWorkFilters.query.trim().toLowerCase()
    if (!q) return true
    const hay = [
      item.title,
      item.status,
      item.assignee,
      item.sourceSystem,
      item.environmentName,
      item.externalId,
    ]
      .filter(Boolean)
      .join(' ')
      .toLowerCase()
    return hay.includes(q)
  })

  const title =
    active === 'Ask'
      ? 'Ask ops'
      : active === 'Knowledge'
        ? 'Knowledge library'
        : active === 'Open work'
          ? 'Unified open work'
          : active === 'Inbox'
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
            <BrandMark className="brand-mark brand-mark-lg" />
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
          {!showRegister && authProviders?.entraExternalId?.enabled && (
            <button type="button" className="ghost" disabled={busy} onClick={() => void onEntraSignIn()}>
              {busy ? 'Opening Microsoft…' : 'Sign in with Microsoft'}
            </button>
          )}
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
            Local pilot: <code>alec.anthony@dnow.com</code> / <code>pilot-demo</code>
            <br />
            Second user: <code>demo@palantir.local</code> / <code>pilot-demo</code>
            {!authProviders?.entraExternalId?.enabled && (
              <>
                <br />
                Microsoft / Entra sign-in appears after External ID is configured (see docs).
              </>
            )}
          </p>
        </form>
      </div>
    )
  }

  return (
    <div className={['shell', navOpen ? 'nav-open' : ''].filter(Boolean).join(' ')}>
      <button
        type="button"
        className="nav-backdrop"
        aria-label="Close menu"
        hidden={!navOpen}
        onClick={() => setNavOpen(false)}
      />
      <aside className="rail" id="app-nav">
        <div className="brand">
          <BrandMark />
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
                onClick={() => {
                  setActive(item)
                  setNavOpen(false)
                  setAskHistoryOpen(false)
                  if (item !== 'Inbox') setInboxComposeOpen(false)
                }}
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
        <div className="rail-footer">
          <p className="rail-user">{userLabel}</p>
          <span className={['pill', health.startsWith('ok') ? 'ok' : ''].filter(Boolean).join(' ')}>
            {health}
          </span>
          <span className={['pill', connectedOutlook ? 'ok' : ''].filter(Boolean).join(' ')}>
            {connectedOutlook ? 'Outlook connected' : 'Outlook off'}
          </span>
          <button type="button" className="sign-out" onClick={onLogout}>
            Sign out
          </button>
        </div>
      </aside>

      <main className="stage">
        <header className="topbar">
          <button
            type="button"
            className="menu-toggle"
            aria-label="Open menu"
            aria-expanded={navOpen}
            aria-controls="app-nav"
            onClick={() => setNavOpen(true)}
          >
            <span className="menu-toggle-bars" aria-hidden="true" />
          </button>
          <div className="topbar-titles">
            <p className="eyebrow">{active}</p>
            <h1>{title}</h1>
          </div>
          <div className="meta">
            <span className="meta-user">{userLabel}</span>
            <span
              className={[
                'meta-outlook',
                'pill',
                connectedOutlook ? 'ok' : '',
              ]
                .filter(Boolean)
                .join(' ')}
            >
              {connectedOutlook
                ? `Outlook · ${connectedOutlook.primaryAddress ?? 'connected'}`
                : 'Outlook · not connected'}
            </span>
            <span className={['meta-health', 'pill', health.startsWith('ok') ? 'ok' : ''].filter(Boolean).join(' ')}>
              {health}
            </span>
            <button type="button" className="sign-out" onClick={onLogout}>
              Sign out
            </button>
          </div>
        </header>

        {(error || statusBanner) && (
          <div className="toast-stack" role="status" aria-live="polite">
            {error && (
              <div className="toast toast-error">
                <p>{error}</p>
                <button
                  type="button"
                  className="toast-dismiss"
                  aria-label="Dismiss error"
                  onClick={() => setError(null)}
                >
                  ×
                </button>
              </div>
            )}
            {statusBanner && (
              <div className="toast toast-ok">
                <p>{statusBanner}</p>
                <button
                  type="button"
                  className="toast-dismiss"
                  aria-label="Dismiss message"
                  onClick={() => setStatusBanner(null)}
                >
                  ×
                </button>
              </div>
            )}
          </div>
        )}

        <div className="stage-scroll">
        {active === 'Ask' && (
          <section
            className={['ask-shell', askHistoryOpen ? 'ask-history-open' : '']
              .filter(Boolean)
              .join(' ')}
          >
            {askHistoryOpen && (
              <button
                type="button"
                className="ask-history-backdrop"
                aria-label="Close chats"
                onClick={() => setAskHistoryOpen(false)}
              />
            )}
            <aside className="ask-history" id="ask-history-panel">
              <div className="ask-history-sheet-bar">
                <strong>Chats</strong>
                <button type="button" className="ghost" onClick={() => setAskHistoryOpen(false)}>
                  Done
                </button>
              </div>
              <button
                type="button"
                className="ask-new-chat"
                disabled={overviewChatBusy}
                onClick={() => {
                  onNewAskChat()
                  setAskHistoryOpen(false)
                }}
              >
                New chat
              </button>
              <div className="ask-history-list">
                {askSessions.length === 0 ? (
                  <p className="muted ask-history-empty">No chats yet</p>
                ) : (
                  askSessions.map((s) => (
                    <div
                      key={s.id}
                      className={`ask-history-item ${askSessionId === s.id ? 'active' : ''}`}
                    >
                      <button
                        type="button"
                        className="ask-history-open"
                        disabled={overviewChatBusy || busy}
                        onClick={() => {
                          void onSelectAskSession(s.id)
                          setAskHistoryOpen(false)
                        }}
                        title={s.title}
                      >
                        <span className="ask-history-title">{s.title}</span>
                        <span className="ask-history-meta">
                          {new Date(s.updatedAt).toLocaleDateString()} · {s.messageCount}
                        </span>
                      </button>
                      <button
                        type="button"
                        className="ghost ask-history-delete"
                        disabled={overviewChatBusy || busy}
                        aria-label="Delete chat"
                        onClick={() => void onDeleteAskSession(s.id)}
                      >
                        ×
                      </button>
                    </div>
                  ))
                )}
              </div>
            </aside>

            <section className="ask-layout">
              <header className="ask-header">
                <div className="ask-header-copy">
                  <h2>Ask</h2>
                  <p className="muted page-lede">
                    Live MaintainX / EZRentOut / Quotes / inventory for each question, plus knowledge
                    and prior Ask chats for learning. Quotes include line items and dollar amounts.
                  </p>
                </div>
                <div className="ask-header-actions">
                  <button
                    type="button"
                    className="ghost ask-history-toggle"
                    disabled={overviewChatBusy}
                    aria-expanded={askHistoryOpen}
                    aria-controls="ask-history-panel"
                    onClick={() => setAskHistoryOpen((open) => !open)}
                  >
                    {askHistoryOpen ? 'Close' : 'Chats'}
                  </button>
                  <button
                    type="button"
                    className="ghost ask-new-chat-inline"
                    disabled={overviewChatBusy}
                    onClick={onNewAskChat}
                  >
                    New
                  </button>
                  <button
                    type="button"
                    className="ghost"
                    disabled={overviewChatBusy}
                    onClick={() => {
                      setAskSourcesOpen((open) => !open)
                    }}
                  >
                    {askSourcesOpen ? 'Hide sources' : 'Sources'}
                  </button>
                </div>
              </header>

              {askSourcesOpen && (
                <div className="ask-sources">
                  <div className="focus-grid">
                    {(
                      [
                        ['includeMaintainX', 'MaintainX work'],
                        ['includeMaintainXInventory', 'MaintainX inventory'],
                        ['includeMonday', 'Monday Quotes'],
                        ['includeEZRentOut', 'EZRentOut'],
                        ['includeInbox', 'Inbox'],
                        ['includeTasks', 'Tasks'],
                        ['includeApprovals', 'Approvals'],
                        ['includeConnectorHealth', 'Connector health'],
                      ] as const
                    ).map(([key, label]) => (
                      <label key={key} className="focus-toggle">
                        <input
                          type="checkbox"
                          checked={overviewFocus[key]}
                          onChange={(e) => {
                            updateOverviewFocus({ [key]: e.target.checked })
                          }}
                        />
                        {label}
                      </label>
                    ))}
                  </div>
                  <div className="ask-sources-row">
                    <label className="focus-depth">
                      Depth
                      <select
                        value={overviewFocus.depth}
                        onChange={(e) => {
                          updateOverviewFocus({
                            depth: e.target.value as OverviewFocus['depth'],
                          })
                        }}
                      >
                        <option value="brief">Brief</option>
                        <option value="standard">Standard</option>
                        <option value="detailed">Detailed</option>
                      </select>
                    </label>
                    <label className="focus-prompt">
                      Personal focus (optional)
                      <textarea
                        value={overviewFocus.customPrompt ?? ''}
                        onChange={(e) => {
                          updateOverviewFocus({ customPrompt: e.target.value })
                        }}
                        placeholder="e.g. Emphasize Permian shop jobs and aging Northern quotes"
                        rows={2}
                      />
                    </label>
                  </div>
                </div>
              )}

              <div className="ask-thread" ref={askThreadRef} aria-live="polite">
                {overviewChat.length === 0 ? (
                  <div className="ask-empty">
                    <p className="ask-empty-lead">What do you need to know?</p>
                    <p className="muted">
                      Open work, rentals, inventory, aging quotes (with dollars & lines), knowledge —
                      ask in plain language.
                    </p>
                    <div className="overview-chat-suggestions">
                      {(
                        [
                          'Give me a brief ops recap for today',
                          'Who has the most physical open work right now?',
                          'What EZRentOut assets are checked out or overdue?',
                          "What's QwikPipe's current daily on-rent run-rate?",
                          "What's QwikPipe's MTD and YTD billed rental revenue?",
                          'EZRentOut revenue last month by customer',
                          'What inventory is out or critically low?',
                          'Which quotes are aging and what are the dollar amounts?',
                        ] as const
                      ).map((q) => (
                        <button
                          key={q}
                          type="button"
                          className="ghost chat-suggest"
                          disabled={overviewChatBusy}
                          onClick={() => void onOverviewChatSend(q)}
                        >
                          {q}
                        </button>
                      ))}
                    </div>
                  </div>
                ) : (
                  overviewChat.map((turn, idx) => {
                    const parsed =
                      turn.role === 'assistant'
                        ? parseKnowledgeSourcesFromContent(turn.content)
                        : { display: turn.content, sources: [] as KnowledgeSource[] }
                    const sources =
                      turn.knowledgeSources && turn.knowledgeSources.length > 0
                        ? turn.knowledgeSources
                        : parsed.sources
                    return (
                    <div
                      key={`${turn.role}-${idx}`}
                      className={`overview-chat-bubble ${turn.role === 'user' ? 'is-user' : 'is-assistant'}`}
                    >
                      <span className="overview-chat-role">{turn.role === 'user' ? 'You' : 'Palantir'}</span>
                      <pre>{parsed.display}</pre>
                      {turn.role === 'assistant' && sources.length > 0 && (
                        <div className="ask-knowledge-sources">
                          <span className="muted ask-knowledge-sources-label">Source documents</span>
                          {sources.map((src) => (
                            <button
                              key={src.documentId}
                              type="button"
                              className="ghost ask-knowledge-download"
                              disabled={busy || overviewChatBusy}
                              onClick={() =>
                                openKnowledgePreview({
                                  documentId: src.documentId,
                                  title: src.title,
                                  fileName: src.fileName,
                                })
                              }
                            >
                              Preview · {src.title}
                            </button>
                          ))}
                        </div>
                      )}
                      {turn.role === 'assistant' && (
                        <div className="actions" style={{ marginTop: '0.5rem' }}>
                          <button
                            type="button"
                            className="ghost"
                            disabled={busy || overviewChatBusy}
                            onClick={() => {
                              const prior = [...overviewChat.slice(0, idx)]
                                .reverse()
                                .find((t) => t.role === 'user')
                              void onSaveAskToKnowledge(parsed.display, prior?.content ?? null)
                            }}
                          >
                            Save to knowledge
                          </button>
                        </div>
                      )}
                    </div>
                    )
                  })
                )}
                {overviewChatBusy && (
                  <div className="ask-thinking" role="status" aria-live="polite">
                    <span className="ask-thinking-label">
                      Thinking
                      <span className="ask-thinking-dots" aria-hidden="true">
                        <span />
                        <span />
                        <span />
                      </span>
                    </span>
                    <span className="ask-thinking-bar" aria-hidden="true" />
                    {askThinkingSeconds >= 8 && (
                      <span className="ask-thinking-hint muted">
                        Still working
                        {askThinkingSeconds >= 60
                          ? ` · ${Math.floor(askThinkingSeconds / 60)}m ${askThinkingSeconds % 60}s`
                          : ` · ${askThinkingSeconds}s`}
                        {askThinkingSeconds >= 90
                          ? ' (live pulls can take a few minutes)'
                          : ''}
                      </span>
                    )}
                  </div>
                )}
              </div>

              <form
                className="ask-compose"
                onSubmit={(e) => {
                  e.preventDefault()
                  void onOverviewChatSend()
                }}
              >
                {askPendingFiles.length > 0 && (
                  <div className="ask-attach-chips">
                    {askPendingFiles.map((file) => (
                      <span key={file.localKey} className="ask-attach-chip">
                        {file.fileName}
                        <span className="muted" style={{ marginLeft: '0.35rem' }}>
                          {file.status === 'uploading'
                            ? 'uploading…'
                            : askExtractPending(file.status)
                              ? 'extracting…'
                              : file.status === 'error'
                                ? 'failed'
                                : file.status.toLowerCase()}
                        </span>
                        <button
                          type="button"
                          className="ghost"
                          disabled={overviewChatBusy}
                          aria-label={`Remove ${file.fileName}`}
                          onClick={() =>
                            setAskPendingFiles((prev) =>
                              prev.filter((c) => c.localKey !== file.localKey),
                            )
                          }
                        >
                          ×
                        </button>
                      </span>
                    ))}
                  </div>
                )}
                <div className="ask-compose-row">
                  <input
                    ref={askFileInputRef}
                    type="file"
                    multiple
                    accept=".pdf,.txt,.md,.csv,.json,.log,.xml,.html,.htm,.zip,text/*,application/pdf,application/json,application/zip"
                    hidden
                    onChange={(e) => onAskFilesChosen(e.target.files)}
                  />
                  <button
                    type="button"
                    className="ghost ask-attach-btn"
                    disabled={overviewChatBusy || askPendingFiles.length >= 5}
                    title="Attach PDF, text, or zip (up to 4 GB) for Ask to review. Say “add to knowledge” to save it."
                    onClick={() => askFileInputRef.current?.click()}
                  >
                    Attach
                  </button>
                  <textarea
                    value={overviewChatDraft}
                    onChange={(e) => setOverviewChatDraft(e.target.value)}
                    placeholder="Ask about open work, rentals, quotes… or attach a file to review"
                    rows={2}
                    disabled={overviewChatBusy}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' && !e.shiftKey) {
                        e.preventDefault()
                        void onOverviewChatSend()
                      }
                    }}
                  />
                  <button
                    type="submit"
                    disabled={
                      overviewChatBusy ||
                      askPendingFiles.some((f) => f.status === 'uploading' || f.status === 'error') ||
                      (!overviewChatDraft.trim() && askPendingFiles.length === 0)
                    }
                  >
                    {overviewChatBusy ? '…' : 'Ask'}
                  </button>
                </div>
                <p className="muted ask-attach-hint">
                  Attach PDF, text, or zip (up to 4 GB). Upload acknowledges immediately; text extract
                  runs in the background. Say “add to knowledge” to save into org knowledge.
                </p>
              </form>
            </section>
          </section>
        )}

        {active === 'Open work' && (
          <section className="open-work">
            <div className="panel open-work-toolbar">
              <div>
                <h2 className="page-section-title">All sources</h2>
                <p className="muted page-lede">
                  MaintainX (both orgs), EZRentOut, and Monday — normalized into one list. Defaults to
                  physical MaintainX work (hides On hold). Use Comment / Update to queue an
                  approval-gated write-back.
                  {openWorkLoadedAt
                    ? ` Last refresh ${new Date(openWorkLoadedAt).toLocaleString()}.`
                    : ''}
                </p>
              </div>
              <button type="button" onClick={() => void onRefreshOpenWork()} disabled={busy || openWorkLoading}>
                {busy || openWorkLoading ? 'Refreshing…' : 'Refresh'}
              </button>
            </div>

            <div className="open-work-filters">
              <label>
                Source
                <select
                  value={openWorkFilters.source}
                  onChange={(e) =>
                    setOpenWorkFilters((prev) => ({ ...prev, source: e.target.value }))
                  }
                >
                  <option value="all">All systems</option>
                  {openWorkSources.map((source) => (
                    <option key={source} value={source}>
                      {source}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Environment
                <select
                  value={openWorkFilters.environment}
                  onChange={(e) =>
                    setOpenWorkFilters((prev) => ({ ...prev, environment: e.target.value }))
                  }
                >
                  <option value="all">All environments</option>
                  {openWorkEnvironments.map((env) => (
                    <option key={env} value={env}>
                      {env}
                    </option>
                  ))}
                </select>
              </label>
              <label className="open-work-overdue">
                <input
                  type="checkbox"
                  checked={openWorkFilters.overdueOnly}
                  onChange={(e) =>
                    setOpenWorkFilters((prev) => ({ ...prev, overdueOnly: e.target.checked }))
                  }
                />
                Overdue only
              </label>
              <label className="open-work-overdue">
                <input
                  type="checkbox"
                  checked={openWorkFilters.physicalOnly}
                  onChange={(e) =>
                    setOpenWorkFilters((prev) => ({ ...prev, physicalOnly: e.target.checked }))
                  }
                />
                Physical only (hide On hold)
              </label>
              <label className="open-work-search">
                Search
                <input
                  value={openWorkFilters.query}
                  onChange={(e) =>
                    setOpenWorkFilters((prev) => ({ ...prev, query: e.target.value }))
                  }
                  placeholder="Title, assignee, id…"
                />
              </label>
            </div>

            <p className="muted open-work-count">
              Showing {filteredOpenWork.length} of {openWorkItems.length}
              {openWorkFilters.overdueOnly
                ? ` · ${openWorkItems.filter((i) => isOverdue(i)).length} overdue total`
                : ''}
            </p>

            {writeBackTarget && (
              <form className="panel write-back-composer" onSubmit={(e) => void onProposeWriteBack(e)}>
                <div>
                  <h2>
                    {writeBackTarget.sourceSystem === 'MaintainX'
                      ? 'Comment for approval'
                      : 'Monday update for approval'}
                  </h2>
                  <p className="muted">
                    {writeBackTarget.title} · #{writeBackTarget.externalId}
                    {writeBackTarget.environmentName
                      ? ` · ${writeBackTarget.environmentName}`
                      : ''}
                  </p>
                </div>
                <textarea
                  value={writeBackBody}
                  onChange={(e) => setWriteBackBody(e.target.value)}
                  rows={4}
                  placeholder={
                    writeBackTarget.sourceSystem === 'MaintainX'
                      ? 'Comment to post on this work order…'
                      : 'Update to post on this Monday item…'
                  }
                  required
                />
                <div className="actions">
                  <button type="submit" disabled={busy || !writeBackBody.trim()}>
                    Request approval
                  </button>
                  <button
                    type="button"
                    className="ghost"
                    onClick={() => {
                      setWriteBackTarget(null)
                      setWriteBackBody('')
                    }}
                    disabled={busy}
                  >
                    Cancel
                  </button>
                </div>
              </form>
            )}

            <div className="list">
              {!openWorkReady ? (
                <PanelWorking label="Loading open work" />
              ) : filteredOpenWork.length === 0 ? (
                <div className="empty">
                  <h2>No open work</h2>
                  <p>
                    {openWorkItems.length === 0
                      ? 'No items from connected ops systems yet. Check Admin → Ops connectors.'
                      : 'Nothing matches the current filters.'}
                  </p>
                </div>
              ) : (
                filteredOpenWork.map((item) => {
                  const overdue = isOverdue(item)
                  return (
                    <article
                      key={`${item.sourceSystem}-${item.environmentName ?? ''}-${item.externalId}`}
                      className={overdue ? 'row open-work-row overdue' : 'row open-work-row'}
                    >
                      <div className="open-work-main">
                        <div className="open-work-meta">
                          <span className={sourceBadgeClass(item.sourceSystem)}>
                            {item.sourceSystem}
                            {item.environmentName ? ` · ${item.environmentName}` : ''}
                          </span>
                          {item.status && <span className="pill">{item.status}</span>}
                          {overdue && <span className="pill warn">Overdue</span>}
                        </div>
                        <h3>
                          {item.url ? (
                            <a href={item.url} target="_blank" rel="noreferrer">
                              {item.title}
                            </a>
                          ) : (
                            item.title
                          )}
                        </h3>
                        <p>
                          {[item.assignee ? `Assignee ${item.assignee}` : null, `#${item.externalId}`]
                            .filter(Boolean)
                            .join(' · ')}
                        </p>
                      </div>
                      <div className="actions">
                        {supportsWriteBack(item) && (
                          <button
                            type="button"
                            className="ghost"
                            onClick={() => {
                              setWriteBackTarget(item)
                              setWriteBackBody('')
                            }}
                            disabled={busy}
                          >
                            {item.sourceSystem === 'MaintainX' ? 'Comment' : 'Update'}
                          </button>
                        )}
                        {item.dueAt && (
                          <time dateTime={item.dueAt}>{new Date(item.dueAt).toLocaleString()}</time>
                        )}
                      </div>
                    </article>
                  )
                })
              )}
            </div>
          </section>
        )}

        {active === 'Inbox' && (
          <section
            className={[
              'inbox-layout',
              selectedId ? 'inbox-show-thread' : 'inbox-show-list',
            ].join(' ')}
          >
            <div className="inbox-list">
              <div className="inbox-toolbar">
                <div className="inbox-toolbar-actions">
                  <button
                    type="button"
                    className="ghost inbox-compose-toggle"
                    onClick={() => setInboxComposeOpen((v) => !v)}
                    aria-expanded={inboxComposeOpen}
                  >
                    {inboxComposeOpen ? 'Cancel' : 'New conversation'}
                  </button>
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
                <form
                  className={['composer', 'inbox-compose', inboxComposeOpen ? 'open' : '']
                    .filter(Boolean)
                    .join(' ')}
                  onSubmit={(e) => {
                    void onCreate(e)
                    setInboxComposeOpen(false)
                  }}
                >
                  <input
                    value={subject}
                    onChange={(e) => setSubject(e.target.value)}
                    placeholder="Start a conversation subject…"
                    aria-label="Conversation subject"
                  />
                  <button type="submit" disabled={busy}>
                    {busy ? 'Creating…' : 'Create'}
                  </button>
                </form>
              </div>

              <div className="list">
                {conversations.length === 0 ? (
                  <div className="empty">
                    <h2>Inbox is empty</h2>
                    <p>
                      {connectedOutlook
                        ? 'Sync Outlook to pull in pilot mail, or start a local conversation.'
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
                        {new Date(item.updatedAt).toLocaleString(undefined, {
                          month: 'short',
                          day: 'numeric',
                          hour: 'numeric',
                          minute: '2-digit',
                        })}
                      </time>
                    </button>
                  ))
                )}
              </div>
            </div>

            <div className="thread">
              {!selected ? (
                <div className="empty inbox-thread-placeholder">
                  <h2>Select a conversation</h2>
                  <p>Messages, internal notes, and claim/release live here.</p>
                </div>
              ) : (
                <>
                  <div className="thread-header">
                    <button
                      type="button"
                      className="ghost thread-back"
                      onClick={() => setSelectedId(null)}
                    >
                      ← Inbox
                    </button>
                    <div className="thread-heading">
                      <h2>{selected.subject || 'Untitled conversation'}</h2>
                      <p>{assigneeLabel(selected, currentUserId)}</p>
                    </div>
                    <div className="actions">
                      <button
                        type="button"
                        className="ghost"
                        onClick={() => setThreadSort(!threadSortNewestFirst)}
                        title="Toggle message order"
                      >
                        {threadSortNewestFirst ? 'Newest' : 'Oldest'}
                      </button>
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
                    {sortedMessages.length === 0 ? (
                      <p className="muted">No messages yet.</p>
                    ) : (
                      sortedMessages.map((msg) => (
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
                    Email replies, ops write-backs, and knowledge captures land here for approval
                    before anything is posted or saved to org memory.
                  </p>
                </div>
              ) : (
                pendingApprovals.map((item) => {
                  const isOps =
                    item.draftKind === 'maintainx.comment' || item.draftKind === 'monday.update'
                  const isKnowledge = item.draftKind === 'knowledge.save'
                  return (
                  <article key={item.id} className="row task-row">
                    <div>
                      <h3>
                        {item.draftSubject ||
                          (isKnowledge
                            ? 'Save to knowledge'
                            : isOps
                              ? 'Ops write-back'
                              : 'Outbound reply')}
                      </h3>
                      <p>
                        {isKnowledge ? 'Store' : isOps ? 'Target' : 'To'}{' '}
                        {item.draftTo || 'unknown'} · {item.status}
                        {isOps || isKnowledge ? ` · ${item.draftKind}` : ''}
                      </p>
                      <p style={{ marginTop: '0.5rem', whiteSpace: 'pre-wrap' }}>{item.draftBody}</p>
                    </div>
                    <div className="actions">
                      <button type="button" onClick={() => void onApprove(item.id)} disabled={busy}>
                        {isKnowledge
                          ? 'Approve & save'
                          : isOps
                            ? 'Approve & post'
                            : 'Approve & send'}
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
                  )
                })
              )}
            </div>
          </section>
        )}

        {active === 'Knowledge' && (
          <section className="knowledge">
            <div className="panel knowledge-panel">
              <div className="knowledge-toolbar">
                <div className="knowledge-toolbar-actions">
                  <button
                    type="button"
                    className="ghost"
                    onClick={() => setKnowledgeUploadOpen(true)}
                    disabled={!knowledgeStatus?.storageConfigured}
                  >
                    Upload
                  </button>
                  <button
                    type="button"
                    className="ghost"
                    onClick={() => void refreshKnowledge()}
                    disabled={busy || knowledgeLoading}
                  >
                    Refresh
                  </button>
                </div>
              </div>
              <p className="muted page-lede knowledge-lede">
                Upload playbooks, diagrams, PDFs, and PLC programs. Files are stored immediately;
                text/PDF/image indexing continues in the background. Docs are auto-sorted into
                collections. Multi-file or .zip (up to 4 GB per file / 4 GB zip).
              </p>
              {!knowledgeReady ? (
                <PanelWorking label="Loading knowledge" />
              ) : !knowledgeStatus?.storageConfigured ? (
                <PanelWorking label="Waiting for storage" />
              ) : knowledgeDocs.length === 0 ? (
                <div className="empty knowledge-empty">
                  <h2>No documents yet</h2>
                  <p>Upload playbooks, PDFs, diagrams, or PLC programs to get started.</p>
                  <button
                    type="button"
                    className="ghost"
                    onClick={() => setKnowledgeUploadOpen(true)}
                    disabled={!knowledgeStatus?.storageConfigured}
                  >
                    Upload files
                  </button>
                </div>
              ) : (
                <div className="knowledge-browse">
                  <div className="knowledge-browse-toolbar">
                    <MenuSelect
                      label="Collection"
                      value={knowledgeBrowseCollection}
                      onChange={(next) => {
                        setKnowledgeBrowseCollection(next)
                        setKnowledgeBrowseFolder('all')
                      }}
                      options={[
                        {
                          value: 'all',
                          label: `All (${(knowledgeLibrary?.documents ?? knowledgeDocs).filter((d) => d.status !== 'Duplicate').length})`,
                        },
                        ...(knowledgeLibrary?.collections ?? []).map((c) => ({
                          value: c.name,
                          label: `${c.name} (${c.documentCount})`,
                        })),
                      ]}
                    />
                    <MenuSelect
                      label="Folder"
                      value={knowledgeBrowseFolder}
                      disabled={knowledgeBrowseCollection === 'all'}
                      onChange={setKnowledgeBrowseFolder}
                      options={[
                        { value: 'all', label: 'All folders' },
                        { value: '__root__', label: '(collection root)' },
                        ...(
                          knowledgeLibrary?.collections.find(
                            (c) => c.name === knowledgeBrowseCollection,
                          )?.folders ?? []
                        ).map((f) => ({ value: f, label: f })),
                      ]}
                    />
                    <label className="knowledge-browse-search">
                      Filter
                      <input
                        value={knowledgeBrowseQuery}
                        onChange={(e) => setKnowledgeBrowseQuery(e.target.value)}
                        placeholder="Title, file, tags…"
                      />
                    </label>
                  </div>

                  {(() => {
                    const source = knowledgeLibrary?.documents ?? knowledgeDocs
                    const q = knowledgeBrowseQuery.trim().toLowerCase()
                    const filtered = source.filter((doc) => {
                      if (doc.status === 'Duplicate') return false
                      if (
                        knowledgeBrowseCollection !== 'all' &&
                        (doc.collection || 'General') !== knowledgeBrowseCollection
                      ) {
                        return false
                      }
                      if (knowledgeBrowseCollection !== 'all' && knowledgeBrowseFolder !== 'all') {
                        const folder = doc.folderPath || ''
                        if (knowledgeBrowseFolder === '__root__') {
                          if (folder) return false
                        } else if (folder !== knowledgeBrowseFolder) {
                          return false
                        }
                      }
                      if (!q) return true
                      const hay = `${doc.title} ${doc.fileName} ${doc.tags || ''} ${doc.collection || ''} ${doc.folderPath || ''}`.toLowerCase()
                      return hay.includes(q)
                    })

                    if (filtered.length === 0) {
                      return (
                        <p className="muted" style={{ marginTop: '0.75rem' }}>
                          No documents in this view.
                        </p>
                      )
                    }

                    let lastGroup = ''
                    return (
                      <ul className="ops-health-list knowledge-browse-list">
                        {filtered.map((doc) => {
                          const group = `${doc.collection || 'General'}${doc.folderPath ? ` / ${doc.folderPath}` : ''}`
                          const showHeading = group !== lastGroup
                          lastGroup = group
                          return (
                            <li key={doc.id} className="knowledge-browse-item">
                              {showHeading && (
                                <div className="knowledge-browse-group">{group}</div>
                              )}
                              <div className="knowledge-browse-row">
                                <span
                                  className={
                                    doc.status === 'Indexed'
                                      ? 'pill ok'
                                      : doc.status === 'Queued' || doc.status === 'Indexing'
                                        ? 'pill warn'
                                        : doc.status === 'IndexFailed'
                                          ? 'pill danger'
                                          : 'pill'
                                  }
                                >
                                  {doc.status === 'Indexing' ? 'Indexing…' : doc.status}
                                </span>
                                <div>
                                  <strong>{doc.title}</strong>
                                  <p className="muted">
                                    {doc.fileName} · {formatBytes(doc.byteSize)}
                                    {doc.chunkCount > 0 ? ` · ${doc.chunkCount} chunks` : ''}
                                    {doc.indexError ? ` — ${doc.indexError}` : ''}
                                  </p>
                                </div>
                                <div className="actions" style={{ display: 'flex', gap: '0.4rem' }}>
                                  <button
                                    type="button"
                                    className="ghost"
                                    onClick={() =>
                                      openKnowledgePreview({
                                        documentId: doc.id,
                                        title: doc.title,
                                        fileName: doc.fileName,
                                      })
                                    }
                                    disabled={busy}
                                  >
                                    Preview
                                  </button>
                                  <button
                                    type="button"
                                    className="ghost"
                                    onClick={() => void onDeleteKnowledge(doc.id)}
                                    disabled={busy}
                                  >
                                    Delete
                                  </button>
                                </div>
                              </div>
                            </li>
                          )
                        })}
                      </ul>
                    )
                  })()}
                </div>
              )}
            </div>
          </section>
        )}

        {active === 'Admin' && (
          <section className="admin">
            <div className="panel">
              <div className="admin-panel-header">
                <div>
                  <h2>Appearance</h2>
                  <p>
                    Primary and secondary colors for this browser. Updates buttons, accents, the
                    sidebar mark, and the tab favicon.
                  </p>
                </div>
                <BrandMark className="brand-mark brand-mark-lg" />
              </div>

              <div className="theme-pickers">
                <label>
                  Primary
                  <span className="theme-color-row">
                    <input
                      type="color"
                      value={
                        /^#[0-9a-fA-F]{6}$/.test(theme.primary)
                          ? theme.primary
                          : DEFAULT_THEME.primary
                      }
                      onChange={(e) => updateTheme({ primary: e.target.value })}
                      aria-label="Primary color"
                    />
                    <input
                      type="text"
                      value={theme.primary}
                      onChange={(e) => {
                        const v = e.target.value.trim()
                        if (/^#[0-9a-fA-F]{6}$/.test(v)) updateTheme({ primary: v })
                        else setTheme((prev) => ({ ...prev, primary: v }))
                      }}
                      onBlur={() => {
                        if (!/^#[0-9a-fA-F]{6}$/.test(theme.primary)) {
                          updateTheme({ primary: DEFAULT_THEME.primary })
                        }
                      }}
                      spellCheck={false}
                    />
                  </span>
                </label>
                <label>
                  Secondary
                  <span className="theme-color-row">
                    <input
                      type="color"
                      value={
                        /^#[0-9a-fA-F]{6}$/.test(theme.secondary)
                          ? theme.secondary
                          : DEFAULT_THEME.secondary
                      }
                      onChange={(e) => updateTheme({ secondary: e.target.value })}
                      aria-label="Secondary color"
                    />
                    <input
                      type="text"
                      value={theme.secondary}
                      onChange={(e) => {
                        const v = e.target.value.trim()
                        if (/^#[0-9a-fA-F]{6}$/.test(v)) updateTheme({ secondary: v })
                        else setTheme((prev) => ({ ...prev, secondary: v }))
                      }}
                      onBlur={() => {
                        if (!/^#[0-9a-fA-F]{6}$/.test(theme.secondary)) {
                          updateTheme({ secondary: DEFAULT_THEME.secondary })
                        }
                      }}
                      spellCheck={false}
                    />
                  </span>
                </label>
              </div>

              <div className="theme-presets">
                {THEME_PRESETS.map((preset) => (
                  <button
                    key={preset.id}
                    type="button"
                    className="theme-preset"
                    onClick={() => applyThemePreset(preset.colors)}
                  >
                    <span
                      className="theme-swatch"
                      style={{
                        background: `linear-gradient(135deg, ${preset.colors.primary}, ${preset.colors.secondary})`,
                      }}
                    />
                    {preset.label}
                  </button>
                ))}
                <button
                  type="button"
                  className="ghost"
                  onClick={() => applyThemePreset(DEFAULT_THEME)}
                >
                  Reset default
                </button>
              </div>
            </div>

            <div className="panel" style={{ marginTop: '1rem' }}>
              <div className="admin-panel-header">
                <div>
                  <h2>AI providers</h2>
                  <p>
                    Multiple models can run side by side. Tasks route to the provider they are best
                    at (Gemini for recap/chat/draft by default; Ollama as local fallback).
                  </p>
                </div>
                <button type="button" className="ghost" onClick={() => void onRefreshOpsHealth()} disabled={busy}>
                  Refresh
                </button>
              </div>
              {!aiStatus ? (
                <p className="muted" style={{ marginTop: '1rem' }}>
                  No AI status yet.
                </p>
              ) : (
                <>
                  <ul className="ops-health-list" style={{ marginTop: '1rem' }}>
                    {aiStatus.providers.map((p) => (
                      <li key={p.name}>
                        <span className={p.configured ? 'pill ok' : 'pill'}>
                          {p.configured ? 'Ready' : 'Off'}
                        </span>
                        <div>
                          <strong>
                            {p.name} · {p.provider}
                          </strong>
                          <p className="muted">
                            {p.model || '(no model)'}
                            {p.detail ? ` — ${p.detail}` : ''}
                          </p>
                        </div>
                      </li>
                    ))}
                  </ul>
                  <h3 style={{ margin: '1.25rem 0 0.5rem', fontSize: '0.95rem' }}>Task routing</h3>
                  <ul className="ops-health-list">
                    {aiStatus.tasks.map((t) => (
                      <li key={t.task}>
                        <span className={t.configured ? 'pill ok' : 'pill warn'}>
                          {t.configured ? 'Routed' : 'Fallback'}
                        </span>
                        <div>
                          <strong>{t.task}</strong>
                          <p className="muted">
                            → {t.providerName} ({t.provider}
                            {t.model ? ` / ${t.model}` : ''})
                          </p>
                        </div>
                      </li>
                    ))}
                  </ul>
                  <p className="muted" style={{ marginTop: '0.85rem' }}>
                    Free Gemini test: <code>dotnet user-secrets set &quot;Ai:Providers:gemini:ApiKey&quot; &quot;…&quot;</code>
                    {' '}then restart API. Corporate key later uses the same setting.
                  </p>
                </>
              )}
            </div>

            <div className="panel" style={{ marginTop: '1rem' }}>
              <div className="admin-panel-header">
                <div>
                  <h2>Knowledge</h2>
                  <p>
                    Browse, upload, and preview org knowledge from the{' '}
                    <button
                      type="button"
                      className="ghost"
                      style={{ padding: '0 0.15rem', verticalAlign: 'baseline' }}
                      onClick={() => setActive('Knowledge')}
                    >
                      Knowledge
                    </button>{' '}
                    tab — collections, folders, and Ask source previews live there.
                  </p>
                </div>
                <button
                  type="button"
                  className="ghost"
                  onClick={() => void refreshKnowledge()}
                  disabled={busy || knowledgeLoading}
                >
                  {knowledgeLoading ? 'Checking…' : 'Check storage'}
                </button>
              </div>
              <p className="muted" style={{ marginTop: '0.75rem' }}>
                Storage:{' '}
                {knowledgeStatus == null && !knowledgeReady ? (
                  <span className="pill">Unknown</span>
                ) : knowledgeStatus?.storageConfigured ? (
                  <span className="pill ok">Configured · {knowledgeStatus.container}</span>
                ) : (
                  <span className="pill warn">Not configured</span>
                )}
              </p>
            </div>

            <div className="panel" style={{ marginTop: '1rem' }}>
              <div className="admin-panel-header">
                <div>
                  <h2>WhatsApp bridge</h2>
                  <p>
                    Read-only ingest of existing internal WhatsApp groups via WAHA (no org process
                    change). Threads appear in Inbox as channel WhatsApp; gaps cross-check open
                    MaintainX / Monday / EZRentOut. Setup: <code>connectors/WhatsApp/README.md</code>
                  </p>
                </div>
                <button type="button" className="ghost" onClick={() => void onRefreshWhatsAppGaps()} disabled={busy}>
                  {busy ? 'Checking…' : 'Refresh gaps'}
                </button>
              </div>
              <p className="muted" style={{ marginTop: '0.75rem' }}>
                {whatsAppStatus ? (
                  <>
                    <span
                      className={
                        whatsAppStatus.configured
                          ? 'pill ok'
                          : whatsAppStatus.enabled
                            ? 'pill warn'
                            : 'pill'
                      }
                    >
                      {whatsAppStatus.configured
                        ? 'Configured'
                        : whatsAppStatus.enabled
                          ? 'Needs secret'
                          : 'Disabled'}
                    </span>{' '}
                    {whatsAppStatus.instanceName} · {whatsAppStatus.detail}
                  </>
                ) : (
                  'Status not loaded yet.'
                )}
              </p>
              {whatsAppGaps.length === 0 ? (
                <p className="muted" style={{ marginTop: '0.75rem' }}>
                  No WhatsApp threads ingested yet — start WAHA, join groups, wait for messages.
                </p>
              ) : (
                <ul className="ops-health-list" style={{ marginTop: '0.75rem' }}>
                  {whatsAppGaps.slice(0, 40).map((g) => (
                    <li key={g.conversationId}>
                      <span
                        className={
                          g.matchStatus === 'Linked'
                            ? 'pill ok'
                            : g.matchStatus === 'Partial'
                              ? 'pill warn'
                              : 'pill danger'
                        }
                      >
                        {g.matchStatus}
                      </span>
                      <div>
                        <strong>{g.subject}</strong>
                        <p className="muted">
                          {g.latestSnippet || '—'}
                          {g.extractedHints.length > 0
                            ? ` · hints: ${g.extractedHints.slice(0, 6).join(', ')}`
                            : ''}
                          {g.matches.length > 0
                            ? ` · ops: ${g.matches
                                .slice(0, 3)
                                .map((m) => `${m.sourceSystem} ${m.title}`)
                                .join('; ')}`
                            : ''}
                        </p>
                      </div>
                      <button
                        type="button"
                        className="ghost"
                        onClick={() => {
                          setActive('Inbox')
                          setSelectedId(g.conversationId)
                        }}
                      >
                        Open
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <div className="panel" style={{ marginTop: '1rem' }}>
              <div className="admin-panel-header">
                <div>
                  <h2>Ops connectors</h2>
                  <p>
                    MaintainX (Permian + Northern), EZRentOut, and Monday.com. Keys live in user
                    secrets / config — this panel only probes connectivity.
                  </p>
                </div>
                <button type="button" onClick={() => void onRefreshOpsHealth()} disabled={busy}>
                  {busy ? 'Checking…' : 'Check health'}
                </button>
              </div>
              {opsHealth.length === 0 ? (
                <p className="muted" style={{ marginTop: '1rem' }}>
                  No health results yet. Click Check health.
                </p>
              ) : (
                <ul className="ops-health-list">
                  {opsHealth.map((h) => (
                    <li key={`${h.connectorType}-${h.instanceName}`}>
                      <span
                        className={
                          h.healthy ? 'pill ok' : h.configured ? 'pill warn' : 'pill'
                        }
                      >
                        {h.healthy ? 'Connected' : h.configured ? 'Issue' : 'Not configured'}
                      </span>
                      <div>
                        <strong>
                          {h.connectorType}
                          {h.instanceName && h.instanceName !== h.connectorType
                            ? ` · ${h.instanceName}`
                            : ''}
                        </strong>
                        <p className="muted">{h.detail || '—'}</p>
                      </div>
                      <time dateTime={h.checkedAt}>
                        {new Date(h.checkedAt).toLocaleTimeString()}
                      </time>
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <div className="panel" style={{ marginTop: '1rem' }}>
              <h2>Connect Outlook</h2>
              <p>
                Connect a Microsoft mailbox (pilot or work). You&apos;ll pick an account at
                Microsoft sign-in — work accounts may route through Okta.
              </p>
              <p className="muted" style={{ marginTop: '0.5rem' }}>
                Known-good pilot: <code>palantir.pilot.aanthony@outlook.com</code>
                <br />
                Work try: sign in as <code>alec.anthony@dnow.com</code> when Microsoft asks.
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
        </div>
      </main>

      {knowledgeUploadOpen && (
        <div
          className="app-modal-backdrop"
          role="presentation"
          onMouseDown={(event) => {
            if (event.target === event.currentTarget && !uploadProgress) {
              setKnowledgeUploadOpen(false)
            }
          }}
        >
          <div
            className="app-modal knowledge-upload-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="knowledge-upload-title"
          >
            <div className="knowledge-upload-modal-header">
              <div>
                <h2 id="knowledge-upload-title">Upload knowledge</h2>
                <p className="muted">
                  PDF, text, images, PLC programs, or .zip (up to 4 GB). Indexing runs in the
                  background after upload.
                </p>
              </div>
              <button
                type="button"
                className="ghost"
                disabled={!!uploadProgress}
                onClick={() => setKnowledgeUploadOpen(false)}
              >
                Close
              </button>
            </div>
            <form
              className="knowledge-upload"
              onSubmit={(e) => {
                e.preventDefault()
                const input = e.currentTarget.elements.namedItem('knowledgeFile') as HTMLInputElement
                void onUploadKnowledge(input.files)
                input.value = ''
              }}
            >
              <label className="app-modal-field">
                <span>Title (optional)</span>
                <input
                  value={knowledgeTitle}
                  onChange={(e) => setKnowledgeTitle(e.target.value)}
                  placeholder="e.g. Shop safety checklist — or batch / zip prefix"
                  disabled={!!uploadProgress}
                />
              </label>
              <label className="app-modal-field">
                <span>Files</span>
                <input
                  name="knowledgeFile"
                  type="file"
                  multiple
                  accept=".txt,.md,.csv,.json,.html,.htm,.log,.xml,.pdf,.png,.jpg,.jpeg,.gif,.webp,.bmp,.acd,.l5x,.l5k,.rss,.st,.scl,.awl,.zip,application/pdf,application/zip,image/*"
                  required
                  disabled={!!uploadProgress}
                />
              </label>
              {uploadProgress && (
                <div
                  className="upload-progress"
                  role="progressbar"
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={Math.round(uploadProgress.percent)}
                  aria-label={
                    uploadProgress.processing
                      ? 'Processing upload on server'
                      : 'Uploading knowledge files'
                  }
                >
                  <div className="upload-progress-meta">
                    <strong>
                      {uploadProgress.processing
                        ? 'Indexing on server…'
                        : `Uploading ${uploadProgress.fileLabel}`}
                    </strong>
                    <span>
                      {uploadProgress.processing
                        ? 'Stored — finishing request'
                        : [
                            `${Math.round(uploadProgress.percent)}%`,
                            `${formatBytes(uploadProgress.loaded)} / ${formatBytes(uploadProgress.total)}`,
                            formatEta(uploadProgress.etaSeconds),
                          ]
                            .filter(Boolean)
                            .join(' · ')}
                    </span>
                  </div>
                  <div
                    className={[
                      'upload-progress-track',
                      uploadProgress.processing ? 'indeterminate' : '',
                    ]
                      .filter(Boolean)
                      .join(' ')}
                  >
                    <div
                      className="upload-progress-fill"
                      style={
                        uploadProgress.processing
                          ? undefined
                          : { width: `${Math.max(2, Math.min(100, uploadProgress.percent))}%` }
                      }
                    />
                  </div>
                </div>
              )}
              <div className="app-modal-actions">
                <button
                  type="button"
                  className="ghost"
                  disabled={!!uploadProgress}
                  onClick={() => setKnowledgeUploadOpen(false)}
                >
                  Cancel
                </button>
                <button type="submit" disabled={busy || !knowledgeStatus?.storageConfigured}>
                  {busy && uploadProgress
                    ? uploadProgress.processing
                      ? 'Processing…'
                      : `Uploading ${Math.round(uploadProgress.percent)}%`
                    : busy
                      ? 'Uploading…'
                      : 'Upload'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {knowledgePreview && (
        <div
          className="app-modal-backdrop knowledge-preview-backdrop"
          role="presentation"
          onMouseDown={(event) => {
            if (event.target === event.currentTarget) closeKnowledgePreview()
          }}
        >
          <div
            className="app-modal knowledge-preview-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="knowledge-preview-title"
          >
            <div className="knowledge-preview-header">
              <div>
                <h2 id="knowledge-preview-title">{knowledgePreview.title}</h2>
                <p className="muted">{knowledgePreview.fileName}</p>
              </div>
              <button type="button" className="ghost" onClick={closeKnowledgePreview}>
                Close
              </button>
            </div>
            <div className="knowledge-preview-body">
              {knowledgePreview.loading && <p className="muted">Loading preview…</p>}
              {!knowledgePreview.loading && knowledgePreview.error && (
                <p className="error">{knowledgePreview.error}</p>
              )}
              {!knowledgePreview.loading && !knowledgePreview.error && knowledgePreview.objectUrl && (
                <>
                  {(() => {
                    const type = (knowledgePreview.contentType || '').toLowerCase()
                    const name = knowledgePreview.fileName.toLowerCase()
                    const isPdf = type.includes('pdf') || name.endsWith('.pdf')
                    const isImage =
                      type.startsWith('image/') ||
                      /\.(png|jpe?g|gif|webp|bmp)$/i.test(name)
                    if (isPdf) {
                      return (
                        <iframe
                          className="knowledge-preview-frame"
                          title={knowledgePreview.title}
                          src={knowledgePreview.objectUrl}
                        />
                      )
                    }
                    if (isImage) {
                      return (
                        <img
                          className="knowledge-preview-image"
                          src={knowledgePreview.objectUrl}
                          alt={knowledgePreview.title}
                        />
                      )
                    }
                    if (knowledgePreview.textPreview != null) {
                      return (
                        <pre className="knowledge-preview-text">{knowledgePreview.textPreview}</pre>
                      )
                    }
                    return (
                      <p className="muted">
                        No inline preview for this file type. Use Download to save and open it
                        locally.
                      </p>
                    )
                  })()}
                </>
              )}
            </div>
            <div className="app-modal-actions knowledge-preview-actions">
              <button type="button" className="ghost" onClick={closeKnowledgePreview}>
                Close
              </button>
              <button
                type="button"
                disabled={
                  knowledgePreview.loading ||
                  !!knowledgePreview.error ||
                  !knowledgePreview.objectUrl
                }
                onClick={() => {
                  void downloadKnowledgeFile(
                    knowledgePreview.documentId,
                    knowledgePreview.fileName,
                  ).catch((err) =>
                    setError(
                      err instanceof Error ? err.message : 'Could not download knowledge file',
                    ),
                  )
                }}
              >
                Download
              </button>
            </div>
          </div>
        </div>
      )}

      {textPrompt && (
        <div
          className="app-modal-backdrop"
          role="presentation"
          onMouseDown={(event) => {
            if (event.target === event.currentTarget) closeTextPrompt(null)
          }}
        >
          <div
            className="app-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="app-text-prompt-title"
          >
            <h2 id="app-text-prompt-title">{textPrompt.heading}</h2>
            {textPrompt.description && <p className="muted">{textPrompt.description}</p>}
            <label className="app-modal-field">
              <span>{textPrompt.label}</span>
              {textPrompt.multiline ? (
                <textarea
                  ref={(el) => {
                    textPromptInputRef.current = el
                  }}
                  rows={5}
                  value={textPrompt.value}
                  onChange={(e) =>
                    setTextPrompt((prev) => (prev ? { ...prev, value: e.target.value } : prev))
                  }
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) {
                      e.preventDefault()
                      closeTextPrompt(textPrompt.value)
                    }
                  }}
                />
              ) : (
                <input
                  ref={(el) => {
                    textPromptInputRef.current = el
                  }}
                  type="text"
                  value={textPrompt.value}
                  onChange={(e) =>
                    setTextPrompt((prev) => (prev ? { ...prev, value: e.target.value } : prev))
                  }
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault()
                      closeTextPrompt(textPrompt.value)
                    }
                  }}
                />
              )}
            </label>
            <div className="app-modal-actions">
              <button type="button" className="ghost" onClick={() => closeTextPrompt(null)}>
                Cancel
              </button>
              <button
                type="button"
                disabled={!textPrompt.value.trim()}
                onClick={() => closeTextPrompt(textPrompt.value)}
              >
                {textPrompt.confirmLabel}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
