import type { ReactNode } from 'react'

/** Normalize AI prose for display without destroying emphasis markers. */
export function prepareAiProse(text: string) {
  return text
    .replace(/\r\n/g, '\n')
    .replace(/^\s*#{1,6}\s+/gm, '')
    .replace(/^\s*\*\s+/gm, '- ')
}

function renderInline(text: string, keyPrefix: string): ReactNode[] {
  // Tokenize in priority order so markers become real formatting.
  const pattern =
    /(`([^`]+)`)|(\*\*([^*]+)\*\*)|(__([^_]+)__)|(\[([^\]]+)\]\((https?:\/\/[^)\s]+)\))|(\*([^*\n]+)\*)|(_([^_\n]+)_)/g

  const nodes: ReactNode[] = []
  let last = 0
  let match: RegExpExecArray | null
  let i = 0

  while ((match = pattern.exec(text)) !== null) {
    if (match.index > last) {
      nodes.push(text.slice(last, match.index))
    }

    const key = `${keyPrefix}-${i++}`
    if (match[2] != null) {
      nodes.push(
        <code key={key} className="ai-prose-code">
          {match[2]}
        </code>,
      )
    } else if (match[4] != null || match[6] != null) {
      nodes.push(<strong key={key}>{match[4] ?? match[6]}</strong>)
    } else if (match[8] != null && match[9] != null) {
      nodes.push(
        <a key={key} href={match[9]} target="_blank" rel="noreferrer">
          {match[8]}
        </a>,
      )
    } else if (match[11] != null || match[13] != null) {
      nodes.push(<em key={key}>{match[11] ?? match[13]}</em>)
    } else {
      nodes.push(match[0])
    }

    last = match.index + match[0].length
  }

  if (last < text.length) {
    nodes.push(text.slice(last))
  }

  return nodes.length > 0 ? nodes : [text]
}

type Block =
  | { type: 'heading'; text: string }
  | { type: 'p'; text: string }
  | { type: 'ul'; items: string[] }
  | { type: 'ol'; items: string[] }

/**
 * Render AI answers with light rich text: headings, bullets, numbered lists,
 * bold, italic, inline code, and links.
 */
export function formatAiProse(text: string, options?: { headings?: boolean }) {
  const allowHeadings = options?.headings !== false
  const lines = prepareAiProse(text)
    .split('\n')
    .map((line) => line.trimEnd())

  const blocks: Block[] = []
  let bullets: string[] = []
  let numbers: string[] = []

  const flushLists = () => {
    if (bullets.length > 0) {
      blocks.push({ type: 'ul', items: bullets })
      bullets = []
    }
    if (numbers.length > 0) {
      blocks.push({ type: 'ol', items: numbers })
      numbers = []
    }
  }

  for (const raw of lines) {
    const line = raw.trim()
    if (!line) {
      flushLists()
      continue
    }

    const bullet = line.match(/^[-–—•▪◦]\s+(.+)$/)
    if (bullet) {
      if (numbers.length > 0) flushLists()
      bullets.push(bullet[1])
      continue
    }

    const numbered = line.match(/^\d+[.)]\s+(.+)$/)
    if (numbered) {
      if (bullets.length > 0) flushLists()
      numbers.push(numbered[1])
      continue
    }

    flushLists()

    const mdHeading = line.match(/^(#{1,3})\s+(.+)$/)
    if (mdHeading) {
      blocks.push({ type: 'heading', text: mdHeading[2] })
      continue
    }

    const looksLikeHeading =
      allowHeadings &&
      line.length <= 56 &&
      !/[.!?]$/.test(line) &&
      !/[*_`\[]/.test(line) &&
      /^[A-Z0-9]/.test(line)

    blocks.push({ type: looksLikeHeading ? 'heading' : 'p', text: line })
  }
  flushLists()

  return (
    <div className="ai-prose">
      {blocks.map((block, index) => {
        if (block.type === 'heading') {
          return (
            <p key={`h-${index}`} className="ai-prose-label">
              {renderInline(block.text, `h${index}`)}
            </p>
          )
        }
        if (block.type === 'ul') {
          return (
            <ul key={`ul-${index}`} className="ai-prose-list">
              {block.items.map((item, itemIndex) => (
                <li key={`li-${index}-${itemIndex}`}>
                  {renderInline(item, `ul${index}-${itemIndex}`)}
                </li>
              ))}
            </ul>
          )
        }
        if (block.type === 'ol') {
          return (
            <ol key={`ol-${index}`} className="ai-prose-list ai-prose-list-ordered">
              {block.items.map((item, itemIndex) => (
                <li key={`oli-${index}-${itemIndex}`}>
                  {renderInline(item, `ol${index}-${itemIndex}`)}
                </li>
              ))}
            </ol>
          )
        }
        return (
          <p key={`p-${index}`} className="ai-prose-p">
            {renderInline(block.text, `p${index}`)}
          </p>
        )
      })}
    </div>
  )
}
