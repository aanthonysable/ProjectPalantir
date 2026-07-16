export const DEMO_ORGANIZATION_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
export const DEMO_USER_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'

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

const headers = (): HeadersInit => ({
  'Content-Type': 'application/json',
  'X-Palantir-User-Id': DEMO_USER_ID,
  'X-Palantir-Organization-Id': DEMO_ORGANIZATION_ID,
})

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      ...headers(),
      ...(init?.headers ?? {}),
    },
  })

  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `Request failed (${response.status})`)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json() as Promise<T>
}

export const getHealth = () =>
  api<{ status: string; service: string }>('/health')

export const getMe = () =>
  api<{ displayName: string; email: string; authMode: string }>('/me')

export const listConversations = () =>
  api<Conversation[]>(`/organizations/${DEMO_ORGANIZATION_ID}/conversations`)

export const createConversation = (subject: string) =>
  api<Conversation>(`/organizations/${DEMO_ORGANIZATION_ID}/conversations`, {
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
      senderUserId: DEMO_USER_ID,
    }),
  })

export const claimConversation = (conversationId: string) =>
  api<Conversation>(`/conversations/${conversationId}/claim`, { method: 'POST' })

export const releaseConversation = (conversationId: string) =>
  api<Conversation>(`/conversations/${conversationId}/release`, { method: 'POST' })

export const listTasks = () =>
  api<TaskItem[]>(`/tasks?organizationId=${DEMO_ORGANIZATION_ID}`)

export const createTask = (title: string, description?: string) =>
  api<TaskItem>('/tasks', {
    method: 'POST',
    body: JSON.stringify({
      organizationId: DEMO_ORGANIZATION_ID,
      title,
      description,
      assignedToUserId: DEMO_USER_ID,
    }),
  })

export const completeTask = (taskId: string) =>
  api<TaskItem>(`/tasks/${taskId}/complete`, { method: 'POST' })
