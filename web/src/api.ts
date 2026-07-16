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
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      ...headers(),
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
      const json = JSON.parse(text) as { error?: string }
      if (json.error) message = json.error
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
}

export type OutlookMessage = {
  id: string
  subject: string | null
  from: string | null
  preview: string | null
  receivedAt: string | null
  isRead: boolean
}

export const beginMicrosoftAuthorize = () =>
  api<{ authorizationUrl: string; state: string }>(
    '/connected-accounts/microsoft/authorize',
    { method: 'POST' },
  )

export const listConnectedAccounts = () =>
  api<ConnectedAccount[]>('/connected-accounts')

export const disconnectAccount = (connectedAccountId: string) =>
  api<void>(`/connected-accounts/${connectedAccountId}`, { method: 'DELETE' })

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
  internalNoteMessageId: string | null
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
