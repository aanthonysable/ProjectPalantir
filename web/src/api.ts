const TOKEN_KEY = 'palantir.accessToken'
const SESSION_KEY = 'palantir.session'

export type SessionUser = {
  userId: string
  organizationId: string
  displayName: string
  email: string
  authMode: string
}

export type Conversation = {
  id: string
  organizationId: string
  subject: string | null
  channel: string
  status: string
  assignedUserId: string | null
  assignedTeamId: string | null
  createdAt: string
  updatedAt: string
  sourceConnectedAccountId?: string | null
  sourceMailboxKind?: string | null
  isUnread?: boolean
}

export type MessageAttachment = {
  id: string
  messageId: string
  fileName: string
  contentType: string
  byteSize: number
  isInline: boolean
  canDownload: boolean
}

export type Message = {
  id: string
  conversationId: string
  direction: string
  body: string | null
  summary: string | null
  senderUserId: string | null
  isInternalNote: boolean
  createdAt: string
  attachments?: MessageAttachment[]
  fromDisplay?: string | null
}

export type TaskItem = {
  id: string
  organizationId: string
  conversationId: string | null
  createdByUserId: string
  assignedToUserId: string | null
  title: string
  description: string | null
  dueAt: string | null
  status: string
  priority: string
  createdAt: string
}

export function getAccessToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

export function getStoredSession(): SessionUser | null {
  const raw = localStorage.getItem(SESSION_KEY)
  if (!raw) return null
  try {
    return JSON.parse(raw) as SessionUser
  } catch {
    return null
  }
}

export function clearSession() {
  localStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(SESSION_KEY)
}

function storeSession(token: string, user: SessionUser) {
  localStorage.setItem(TOKEN_KEY, token)
  localStorage.setItem(SESSION_KEY, JSON.stringify(user))
}

const headers = (): HeadersInit => {
  const h: Record<string, string> = {
    'Content-Type': 'application/json',
  }
  const token = getAccessToken()
  if (token) {
    h.Authorization = `Bearer ${token}`
  }
  return h
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const isFormData = typeof FormData !== 'undefined' && init?.body instanceof FormData
  const baseHeaders = headers() as Record<string, string>
  if (isFormData) {
    delete baseHeaders['Content-Type']
  }

  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      ...baseHeaders,
      ...(init?.headers ?? {}),
    },
  })

  if (response.status === 401) {
    clearSession()
    throw new ApiError(401, 'Sign in required')
  }

  if (!response.ok) {
    const text = await response.text()
    let message = text || `Request failed (${response.status})`
    try {
      const json = JSON.parse(text) as {
        error?: string
        detail?: string
        title?: string
        message?: string
      }
      message =
        json.error || json.detail || json.message || json.title || message
    } catch {
      // keep raw text
    }
    throw new ApiError(response.status, message)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json() as Promise<T>
}

export type LoginResult = SessionUser & {
  accessToken: string
  expiresAt: string
}

export type EntraProviderConfig = {
  enabled: boolean
  authority: string
  clientId: string
  audience: string
  tenantId: string
  scopes: string[]
}

export type AuthProviders = {
  localPasswordEnabled: boolean
  entraExternalId: EntraProviderConfig | null
}

export const getAuthProviders = () => api<AuthProviders>('/auth/providers')

export const login = async (email: string, password: string) => {
  const result = await api<LoginResult>('/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  })
  storeSession(result.accessToken, {
    userId: result.userId,
    organizationId: result.organizationId,
    displayName: result.displayName,
    email: result.email,
    authMode: result.authMode,
  })
  return result
}

export const registerPilotUser = async (
  email: string,
  password: string,
  displayName: string,
) => {
  const result = await api<LoginResult>('/auth/register', {
    method: 'POST',
    body: JSON.stringify({ email, password, displayName }),
  })
  storeSession(result.accessToken, {
    userId: result.userId,
    organizationId: result.organizationId,
    displayName: result.displayName,
    email: result.email,
    authMode: result.authMode,
  })
  return result
}

export const exchangeEntraToken = async (tokens: {
  idToken?: string
  accessToken?: string
}) => {
  const result = await api<LoginResult>('/auth/entra/exchange', {
    method: 'POST',
    body: JSON.stringify({
      idToken: tokens.idToken ?? null,
      accessToken: tokens.accessToken ?? null,
    }),
  })
  storeSession(result.accessToken, {
    userId: result.userId,
    organizationId: result.organizationId,
    displayName: result.displayName,
    email: result.email,
    authMode: result.authMode,
  })
  return result
}

export const logout = () => {
  clearSession()
}

export const getHealth = () =>
  api<{ status: string; service: string }>('/health')

export const getMe = () =>
  api<{
    userId: string
    organizationId: string
    displayName: string
    email: string
    authMode: string
  }>('/me')

function orgId(): string {
  const session = getStoredSession()
  if (!session?.organizationId) throw new Error('Not signed in')
  return session.organizationId
}

function userId(): string {
  const session = getStoredSession()
  if (!session?.userId) throw new Error('Not signed in')
  return session.userId
}

export const listConversations = () =>
  api<Conversation[]>(`/organizations/${orgId()}/conversations`)

export const createConversation = (subject: string) =>
  api<Conversation>(`/organizations/${orgId()}/conversations`, {
    method: 'POST',
    body: JSON.stringify({
      channel: 'Internal',
      subject,
    }),
  })

export const getConversation = (id: string) =>
  api<Conversation>(`/conversations/${id}`)

export const listMessages = (conversationId: string) =>
  api<Message[]>(`/conversations/${conversationId}/messages`)

export async function downloadMessageAttachment(
  conversationId: string,
  messageId: string,
  attachmentId: string,
  fileName: string,
): Promise<void> {
  const token = getAccessToken()
  const response = await fetch(
    `/api/conversations/${conversationId}/messages/${messageId}/attachments/${attachmentId}`,
    {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    },
  )
  if (response.status === 401) {
    clearSession()
    throw new ApiError(401, 'Session expired — sign in again.')
  }
  if (!response.ok) {
    const text = await response.text()
    let message = text || `Download failed (${response.status})`
    try {
      const parsed = JSON.parse(text) as { error?: string }
      if (parsed.error) message = parsed.error
    } catch {
      /* keep message */
    }
    throw new ApiError(response.status, message)
  }

  const blob = await response.blob()
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName || 'attachment'
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
}

export const addMessage = (
  conversationId: string,
  body: string,
  options?: { isInternalNote?: boolean; direction?: string },
) =>
  api<Message>(`/conversations/${conversationId}/messages`, {
    method: 'POST',
    body: JSON.stringify({
      direction: options?.direction ?? 'Outbound',
      body,
      isInternalNote: options?.isInternalNote ?? false,
      senderUserId: userId(),
    }),
  })

export const claimConversation = (conversationId: string) =>
  api<Conversation>(`/conversations/${conversationId}/claim`, { method: 'POST' })

export const releaseConversation = (conversationId: string) =>
  api<Conversation>(`/conversations/${conversationId}/release`, { method: 'POST' })

export const listTasks = () =>
  api<TaskItem[]>(`/tasks?organizationId=${orgId()}`)

export const createTask = (title: string, description?: string) =>
  api<TaskItem>('/tasks', {
    method: 'POST',
    body: JSON.stringify({
      organizationId: orgId(),
      title,
      description,
      assignedToUserId: userId(),
    }),
  })

export const completeTask = (taskId: string) =>
  api<TaskItem>(`/tasks/${taskId}/complete`, { method: 'POST' })

export type FollowUpScanResult = {
  organizationId: string
  conversationsReviewed: number
  proposals: number
  tasksCreated: number
  notes: string[]
}

export const scanFollowUps = () =>
  api<FollowUpScanResult>('/follow-ups/scan', { method: 'POST' })

export type CustomerSummary = {
  id: string
  name: string
  contactCount: number
  conversationCount: number
  openTaskCount: number
  workOrderCount: number
  quoteCount: number
  rentalCount: number
  orderCount: number
  lastActivityAt: string | null
}

export type CustomerContact = {
  id: string
  customerId: string | null
  displayName: string
  email: string | null
  phone: string | null
}

export type CustomerActivity = {
  kind: string
  title: string
  detail: string | null
  occurredAt: string | null
  url: string | null
  conversationId: string | null
  sourceSystem: string | null
}

export type CustomerDetail = {
  id: string
  name: string
  contacts: CustomerContact[]
  activity: CustomerActivity[]
  conversationCount: number
  openTaskCount: number
  workOrderCount: number
  quoteCount: number
  rentalCount: number
  orderCount: number
  companyOverview: string | null
  overviewGeneratedAt: string | null
}

export type CustomerCompanyOverview = {
  customerId: string
  name: string
  overview: string
  generatedAt: string
  fromCache: boolean
  sourceNote: string | null
}

export type CustomerReconcileResult = {
  customersUpserted: number
  contactsUpserted: number
  conversationsLinked: number
  notes: string[]
}

export const listCustomers = () => api<CustomerSummary[]>('/customers')

export const getCustomer = (customerId: string) =>
  api<CustomerDetail>(`/customers/${customerId}`)

export const getCustomerOverview = (customerId: string, refresh = false) =>
  api<CustomerCompanyOverview>(
    `/customers/${customerId}/overview${refresh ? '?refresh=true' : ''}`,
  )

export const createCustomer = (name: string) =>
  api<CustomerSummary>('/customers', {
    method: 'POST',
    body: JSON.stringify({ name }),
  })

export const reconcileCustomers = () =>
  api<CustomerReconcileResult>('/customers/reconcile', { method: 'POST' })

export const warmCustomers = () =>
  api<{ customersUpdated: number }>('/customers/warm', { method: 'POST' })

export type CalendarEvent = {
  id: string
  subject: string | null
  organizer: string | null
  start: string | null
  end: string | null
  location: string | null
  isAllDay: boolean
  webLink: string | null
}

export type TeamsChat = {
  id: string
  topic: string | null
  chatType: string
  lastUpdated: string | null
  lastPreview: string | null
  webUrl: string | null
}

export const listCalendarEvents = (connectedAccountId: string, top = 25) =>
  api<CalendarEvent[]>(`/connected-accounts/${connectedAccountId}/calendar?top=${top}`)

export const listTeamsChats = (connectedAccountId: string, top = 20) =>
  api<TeamsChat[]>(`/connected-accounts/${connectedAccountId}/teams/chats?top=${top}`)

export type ConnectedAccount = {
  id: string
  userId: string
  provider: string
  displayName: string | null
  primaryAddress: string | null
  connectionStatus: string
  grantedScopesJson: string | null
  lastSuccessfulSyncAt: string | null
  updatedAt: string
  mailboxKind?: string
}

export type OutlookMessage = {
  id: string
  subject: string | null
  from: string | null
  preview: string | null
  receivedAt: string | null
  isRead: boolean
}

export const beginMicrosoftAuthorize = (mailboxKind: 'Work' | 'Personal' = 'Work') =>
  api<{ authorizationUrl: string; state: string }>(
    '/connected-accounts/microsoft/authorize',
    {
      method: 'POST',
      body: JSON.stringify({ mailboxKind }),
    },
  )

export const listConnectedAccounts = () =>
  api<ConnectedAccount[]>('/connected-accounts')

export const disconnectAccount = (connectedAccountId: string) =>
  api<void>(`/connected-accounts/${connectedAccountId}`, { method: 'DELETE' })

export const updateMailboxKind = (
  connectedAccountId: string,
  mailboxKind: 'Work' | 'Personal',
) =>
  api<ConnectedAccount>(`/connected-accounts/${connectedAccountId}/mailbox-kind`, {
    method: 'PATCH',
    body: JSON.stringify({ mailboxKind }),
  })

export const listOutlookMail = (connectedAccountId: string, top = 15) =>
  api<OutlookMessage[]>(`/connected-accounts/${connectedAccountId}/mail?top=${top}`)

export type OutlookSyncResult = {
  connectedAccountId: string
  fetched: number
  imported: number
  skipped: number
  conversationIds: string[]
}

export const syncOutlookInbox = (connectedAccountId: string, top = 25) =>
  api<OutlookSyncResult>(`/connected-accounts/${connectedAccountId}/sync?top=${top}`, {
    method: 'POST',
  })

export type ApprovalItem = {
  id: string
  draftId: string | null
  requestedForUserId: string
  status: string
  requestedAt: string
  completedAt: string | null
  completedByUserId: string | null
  draftBody: string | null
  draftSubject: string | null
  draftTo: string | null
  conversationId: string | null
  draftKind: string | null
}

export type ReplyDraftResult = {
  draftId: string
  approvalId: string
  conversationId: string
  toAddress: string
  subject: string
  body: string
  approvalStatus: string
}

export const listApprovals = () => api<ApprovalItem[]>('/approvals')

export const approveRequest = (approvalId: string) =>
  api<ReplyDraftResult>(`/approvals/${approvalId}/approve`, { method: 'POST' })

export const rejectRequest = (approvalId: string) =>
  api<ApprovalItem>(`/approvals/${approvalId}/reject`, { method: 'POST' })

export const createReplyForApproval = (conversationId: string, body: string) =>
  api<ReplyDraftResult>(`/conversations/${conversationId}/reply-for-approval`, {
    method: 'POST',
    body: JSON.stringify({ body }),
  })

export type ConversationSummaryResult = {
  conversationId: string
  summary: string
}

export const summarizeConversation = (conversationId: string) =>
  api<ConversationSummaryResult>(`/conversations/${conversationId}/ai/summarize`, {
    method: 'POST',
  })

export const draftReplyWithAi = (conversationId: string, guidance?: string) =>
  api<ReplyDraftResult>(`/conversations/${conversationId}/ai/draft-reply`, {
    method: 'POST',
    body: JSON.stringify({ guidance: guidance || null }),
  })

export type OverviewFocus = {
  includeInbox: boolean
  includeTasks: boolean
  includeApprovals: boolean
  includeMaintainX: boolean
  includeMaintainXInventory: boolean
  includeEZRentOut: boolean
  includeMonday: boolean
  includeConnectorHealth: boolean
  customPrompt?: string | null
  depth: 'brief' | 'standard' | 'detailed'
}

export type OverviewCounts = {
  conversations: number
  openTasks: number
  pendingApprovals: number
  externalOpenWork: number
  onHoldAwaitingClose: number
  recentlyCompleted: number
  agingQuotes: number
  quotesWithMaintainXLink: number
  inventoryOut: number
  inventoryLow: number
}

export type ConnectorHealth = {
  connectorType: string
  instanceName: string
  configured: boolean
  healthy: boolean
  detail: string | null
  checkedAt: string
}

export type ExternalWorkItem = {
  sourceSystem: string
  environmentName: string | null
  externalId: string
  title: string
  status: string | null
  assignee: string | null
  dueAt: string | null
  url: string | null
  metadata?: Record<string, string> | null
}

export type OverviewListItem = {
  id: string
  title: string
  subtitle: string | null
  status: string | null
  at: string | null
}

export type OverviewSnapshot = {
  generatedAt: string
  completionWindowLabel: string
  counts: OverviewCounts
  connectorHealth: ConnectorHealth[]
  externalWorkSample: ExternalWorkItem[]
  recentlyCompleted: ExternalWorkItem[]
  quotesSample: ExternalWorkItem[]
  inventoryAlerts: InventoryAlert[]
  recentConversations: OverviewListItem[]
  openTasks: OverviewListItem[]
  pendingApprovals: OverviewListItem[]
  notes: string[]
}

export type InventoryAlert = {
  environmentName: string
  partId: string
  name: string
  severity: string
  availableQuantity: number
  minimumQuantity: number
  area: string | null
  partTypes: string | null
}

export type OverviewRecap = {
  generatedAt: string
  narrative: string
  snapshot: OverviewSnapshot
  focusUsed: OverviewFocus
}

export const defaultOverviewFocus = (): OverviewFocus => ({
  includeInbox: false,
  includeTasks: false,
  includeApprovals: false,
  includeMaintainX: true,
  includeMaintainXInventory: true,
  includeEZRentOut: true,
  includeMonday: true,
  includeConnectorHealth: true,
  customPrompt: '',
  depth: 'detailed',
})

export const getOverviewSnapshot = (focus: OverviewFocus) => {
  const q = new URLSearchParams({
    includeInbox: String(focus.includeInbox),
    includeTasks: String(focus.includeTasks),
    includeApprovals: String(focus.includeApprovals),
    includeMaintainX: String(focus.includeMaintainX),
    includeMaintainXInventory: String(focus.includeMaintainXInventory),
    includeEZRentOut: String(focus.includeEZRentOut),
    includeMonday: String(focus.includeMonday),
    includeConnectorHealth: String(focus.includeConnectorHealth),
  })
  return api<OverviewSnapshot>(`/overview?${q}`)
}

export const generateOverviewRecap = (focus: OverviewFocus) =>
  api<OverviewRecap>('/overview/recap', {
    method: 'POST',
    body: JSON.stringify(focus),
  })

export type OverviewChatReply = {
  generatedAt: string
  reply: string
  snapshot: OverviewSnapshot
  focusUsed: OverviewFocus
  sessionId: string
  knowledgeSources?: KnowledgeSource[] | null
}

export type KnowledgeSource = {
  documentId: string
  title: string
  fileName: string
}

export type OverviewChatTurn = {
  role: 'user' | 'assistant'
  content: string
  knowledgeSources?: KnowledgeSource[] | null
}

export const askOverviewChat = (
  focus: OverviewFocus,
  messages: OverviewChatTurn[],
  refreshFacts = false,
  sessionId?: string | null,
  attachmentIds?: string[],
) =>
  api<OverviewChatReply>('/overview/chat', {
    method: 'POST',
    body: JSON.stringify({
      focus,
      messages,
      refreshFacts,
      sessionId: sessionId || null,
      attachmentIds: attachmentIds?.length ? attachmentIds : [],
    }),
  })

export type AskAttachment = {
  id: string
  fileName: string
  contentType: string
  byteSize: number
  extractStatus: string
  extractedChars: number
  sessionId?: string | null
  knowledgeDocumentId?: string | null
  createdAt: string
}

export type AskAttachmentPromoteResult = {
  attachment: AskAttachment
  knowledge: KnowledgeUploadResult | null
}

export const uploadAskAttachments = (files: File[], sessionId?: string | null) => {
  const form = new FormData()
  for (const file of files) {
    form.append('files', file)
  }
  if (sessionId) {
    form.append('sessionId', sessionId)
  }
  return api<AskAttachment[]>('/ask/attachments', {
    method: 'POST',
    body: form,
  })
}

export const getAskAttachments = (ids: string[]) => {
  const q = new URLSearchParams()
  for (const id of ids) {
    q.append('ids', id)
  }
  return api<AskAttachment[]>(`/ask/attachments?${q}`)
}

export const promoteAskAttachment = (attachmentId: string, title?: string) =>
  api<AskAttachmentPromoteResult>(`/ask/attachments/${attachmentId}/promote`, {
    method: 'POST',
    body: JSON.stringify({ title: title || null }),
  })

export type AskSessionSummary = {
  id: string
  title: string
  createdAt: string
  updatedAt: string
  messageCount: number
}

export type AskMessageDto = {
  id: string
  role: 'user' | 'assistant' | string
  content: string
  ordinal: number
  createdAt: string
}

export type AskSessionDetail = {
  id: string
  title: string
  createdAt: string
  updatedAt: string
  messages: AskMessageDto[]
}

export const listAskSessions = () => api<AskSessionSummary[]>('/ask/sessions')

export const getAskSession = (sessionId: string) =>
  api<AskSessionDetail>(`/ask/sessions/${sessionId}`)

export const deleteAskSession = (sessionId: string) =>
  api<void>(`/ask/sessions/${sessionId}`, { method: 'DELETE' })

export type AiProviderStatus = {
  name: string
  provider: string
  model: string
  configured: boolean
  detail: string | null
}

export type AiRoutingStatus = {
  task: string
  providerName: string
  provider: string
  model: string
  configured: boolean
}

export type AiStatus = {
  anyConfigured: boolean
  providers: AiProviderStatus[]
  tasks: AiRoutingStatus[]
}

export const getAiStatus = () => api<AiStatus>('/ai/status')

export const getOpsHealth = () => api<ConnectorHealth[]>('/ops/health')

export const getOpsOpenWork = (live = false) =>
  api<ExternalWorkItem[]>(`/ops/open-work${live ? '?live=true' : ''}`)

export type WhatsAppBridgeStatus = {
  enabled: boolean
  configured: boolean
  instanceName: string
  detail: string
  whatsAppConversationCount: number
  whatsAppMessageCount: number
}

export type WhatsAppOpsMatch = {
  sourceSystem: string
  environmentName: string
  externalId: string
  title: string
  matchMethod: string
  url?: string | null
}

export type WhatsAppGap = {
  conversationId: string
  subject: string
  updatedAt: string
  matchStatus: string
  latestSnippet: string
  extractedHints: string[]
  matches: WhatsAppOpsMatch[]
}

export type WhatsAppOpsCandidate = {
  externalId: string
  title: string
  url?: string | null
  matchMethod: string
  score: number
  confidence: string
}

export type WhatsAppOpsConnectorPill = {
  sourceSystem: string
  /** Matched | Possible | NoMatch */
  status: string
  label: string
  url?: string | null
  candidates: WhatsAppOpsCandidate[]
}

export type WhatsAppMessageOps = {
  messageId: string
  extractedHints: string[]
  connectors: WhatsAppOpsConnectorPill[]
}

export type WhatsAppConversationOps = {
  conversationId: string
  messages: WhatsAppMessageOps[]
}

export const getWhatsAppStatus = () => api<WhatsAppBridgeStatus>('/whatsapp/status')

export const getWhatsAppGaps = () => api<WhatsAppGap[]>('/whatsapp/gaps')

export const analyzeWhatsAppConversation = (conversationId: string) =>
  api<WhatsAppConversationOps>(`/whatsapp/conversations/${conversationId}/ops-matches`)

export const refreshWhatsAppTitles = () =>
  api<{ updated: number }>('/whatsapp/refresh-titles', { method: 'POST' })

export const proposeOpsWriteBack = (input: {
  sourceSystem: string
  environmentName?: string | null
  externalId: string
  title: string
  body: string
}) =>
  api<ReplyDraftResult>('/ops/write-back', {
    method: 'POST',
    body: JSON.stringify({
      sourceSystem: input.sourceSystem,
      environmentName: input.environmentName ?? null,
      externalId: input.externalId,
      title: input.title,
      body: input.body,
    }),
  })

export type KnowledgeDocument = {
  id: string
  title: string
  fileName: string
  contentType: string
  byteSize: number
  status: string
  indexError: string | null
  tags: string | null
  collection: string
  folderPath: string | null
  contentHash?: string | null
  duplicateOfDocumentId?: string | null
  chunkCount: number
  createdAt: string
  updatedAt: string
}

export type KnowledgeCollection = {
  name: string
  documentCount: number
  folders: string[]
}

export type KnowledgeLibrary = {
  collections: KnowledgeCollection[]
  documents: KnowledgeDocument[]
}

export type KnowledgeUploadResult = {
  document: KnowledgeDocument
  indexed: boolean
}

export type KnowledgeUploadBatchResult = {
  results: KnowledgeUploadResult[]
  skippedEntries: number
  notes: string[]
}

export type KnowledgeStatus = {
  storageConfigured: boolean
  container: string
}

export type UploadProgress = {
  loaded: number
  total: number
  percent: number
  /** True once the request body has finished sending and we're waiting on the server. */
  processing: boolean
}

export const getKnowledgeStatus = () => api<KnowledgeStatus>('/knowledge/status')

export const listKnowledgeDocuments = () => api<KnowledgeDocument[]>('/knowledge')

export const getKnowledgeLibrary = () => api<KnowledgeLibrary>('/knowledge/library')

export const uploadKnowledgeDocument = (
  file: File,
  title?: string,
  onProgress?: (progress: UploadProgress) => void,
) => uploadKnowledgeDocuments([file], title, onProgress)

export const uploadKnowledgeDocuments = (
  files: File[],
  title?: string,
  onProgress?: (progress: UploadProgress) => void,
) => {
  const form = new FormData()
  for (const file of files) {
    form.append('files', file)
  }
  if (title?.trim()) {
    form.append('title', title.trim())
  }

  if (!onProgress) {
    return api<KnowledgeUploadBatchResult>('/knowledge/upload', {
      method: 'POST',
      body: form,
    })
  }

  return uploadWithProgress<KnowledgeUploadBatchResult>('/knowledge/upload', form, onProgress)
}

function uploadWithProgress<T>(
  path: string,
  form: FormData,
  onProgress: (progress: UploadProgress) => void,
): Promise<T> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', `/api${path}`)

    const authHeaders = headers() as Record<string, string>
    for (const [key, value] of Object.entries(authHeaders)) {
      if (key.toLowerCase() === 'content-type') continue
      xhr.setRequestHeader(key, value)
    }

    xhr.upload.onprogress = (event) => {
      if (!event.lengthComputable) return
      const percent = event.total > 0 ? Math.min(100, (event.loaded / event.total) * 100) : 0
      onProgress({
        loaded: event.loaded,
        total: event.total,
        percent,
        processing: false,
      })
    }

    xhr.upload.onload = () => {
      onProgress({
        loaded: 1,
        total: 1,
        percent: 100,
        processing: true,
      })
    }

    xhr.onload = () => {
      if (xhr.status === 401) {
        clearSession()
        reject(new ApiError(401, 'Sign in required'))
        return
      }

      if (xhr.status < 200 || xhr.status >= 300) {
        const text = xhr.responseText || `Request failed (${xhr.status})`
        let message = text
        try {
          const json = JSON.parse(text) as {
            error?: string
            detail?: string
            title?: string
            message?: string
          }
          message = json.error || json.detail || json.message || json.title || message
        } catch {
          // keep raw text
        }
        reject(new ApiError(xhr.status, message))
        return
      }

      if (xhr.status === 204 || !xhr.responseText) {
        resolve(undefined as T)
        return
      }

      try {
        resolve(JSON.parse(xhr.responseText) as T)
      } catch {
        reject(new ApiError(xhr.status, 'Invalid JSON response from upload'))
      }
    }

    xhr.onerror = () => reject(new ApiError(0, 'Upload failed — network error'))
    xhr.onabort = () => reject(new ApiError(0, 'Upload cancelled'))
    xhr.send(form)
  })
}

export const deleteKnowledgeDocument = (documentId: string) =>
  api<void>(`/knowledge/${documentId}`, { method: 'DELETE' })

export type KnowledgeFileBlob = {
  blob: Blob
  fileName: string
  contentType: string
  objectUrl: string
}

/** Fetch the original knowledge blob (for preview or download). Caller must revoke objectUrl. */
export async function fetchKnowledgeFileBlob(
  documentId: string,
  fileName?: string,
): Promise<KnowledgeFileBlob> {
  const token = getAccessToken()
  const response = await fetch(`/api/knowledge/${documentId}/file`, {
    headers: token ? { Authorization: `Bearer ${token}` } : undefined,
  })
  if (response.status === 401) {
    clearSession()
    throw new ApiError(401, 'Session expired — sign in again.')
  }
  if (!response.ok) {
    const text = await response.text()
    let message = text || `Download failed (${response.status})`
    try {
      const parsed = JSON.parse(text) as { error?: string }
      if (parsed.error) message = parsed.error
    } catch {
      /* keep message */
    }
    throw new ApiError(response.status, message)
  }

  const blob = await response.blob()
  const disposition = response.headers.get('Content-Disposition') || ''
  const matched = /filename\*?=(?:UTF-8''|")?([^\";]+)/i.exec(disposition)
  const resolvedName =
    fileName ||
    (matched ? decodeURIComponent(matched[1].replace(/"/g, '')) : null) ||
    'knowledge-file'
  const contentType =
    response.headers.get('Content-Type') || blob.type || 'application/octet-stream'

  return {
    blob,
    fileName: resolvedName,
    contentType,
    objectUrl: URL.createObjectURL(blob),
  }
}

/** Download the original knowledge blob (PDF, etc.) with auth. */
export async function downloadKnowledgeFile(
  documentId: string,
  fileName?: string,
): Promise<void> {
  const file = await fetchKnowledgeFileBlob(documentId, fileName)
  const a = document.createElement('a')
  a.href = file.objectUrl
  a.download = file.fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(file.objectUrl)
}

export const proposeKnowledgeCapture = (input: {
  title: string
  body: string
  sourceQuestion?: string | null
  createdByAi?: boolean
}) =>
  api<ReplyDraftResult>('/knowledge/capture', {
    method: 'POST',
    body: JSON.stringify({
      title: input.title,
      body: input.body,
      sourceQuestion: input.sourceQuestion ?? null,
      createdByAi: input.createdByAi ?? true,
    }),
  })
