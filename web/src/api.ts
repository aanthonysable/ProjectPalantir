export const DEMO_ORGANIZATION_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
export const DEMO_USER_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'

export type Conversation = {
  id: string
  organizationId: string
  subject: string | null
  channel: string
  status: string
  assignedUserId: string | null
  createdAt: string
  updatedAt: string
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
      assignedUserId: DEMO_USER_ID,
    }),
  })
