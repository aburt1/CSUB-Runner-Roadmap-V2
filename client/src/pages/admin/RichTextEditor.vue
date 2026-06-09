<script setup lang="ts">
import { useEditor, EditorContent } from '@tiptap/vue-3'
import StarterKit from '@tiptap/starter-kit'
import Link from '@tiptap/extension-link'
import Underline from '@tiptap/extension-underline'
import { ref, watch, nextTick } from 'vue'

const props = defineProps<{
  value: string
}>()

const emit = defineEmits<{
  (e: 'change', html: string): void
}>()

const editor = useEditor({
  extensions: [
    StarterKit.configure({
      heading: { levels: [2, 3] },
      link: false,
    }),
    Underline,
    Link.configure({
      openOnClick: false,
      HTMLAttributes: {
        target: '_blank',
        rel: 'noopener noreferrer',
      },
    }),
  ],
  content: props.value || '',
  onUpdate: ({ editor: ed }) => {
    const html = ed.getHTML()
    emit('change', html === '<p></p>' ? '' : html)
  },
  editorProps: {
    attributes: {
      class:
        'prose prose-sm max-w-none font-body text-sm text-csub-blue-dark px-3 py-2 min-h-[150px] focus:outline-none',
    },
  },
})

watch(
  () => props.value,
  (value) => {
    const ed = editor.value
    if (ed && value !== ed.getHTML() && value !== undefined) {
      ed.commands.setContent(value || '')
    }
  },
)

// Link input
const showLinkInput = ref(false)
const url = ref('')
const linkInputRef = ref<HTMLInputElement | null>(null)

const openLinkInput = () => {
  showLinkInput.value = !showLinkInput.value
  if (showLinkInput.value) {
    const ed = editor.value
    const previousUrl = ed?.getAttributes('link').href as string | undefined
    url.value = previousUrl || 'https://'
    nextTick(() => {
      linkInputRef.value?.focus()
      linkInputRef.value?.select()
    })
  }
}

const closeLinkInput = () => {
  showLinkInput.value = false
}

const applyLink = () => {
  const ed = editor.value
  if (!ed) return

  if (!url.value || url.value === 'https://') {
    ed.chain().focus().extendMarkRange('link').unsetLink().run()
    closeLinkInput()
    return
  }

  const { from, to } = ed.state.selection
  if (from === to) {
    ed.chain()
      .focus()
      .insertContent(
        `<a href="${url.value}" target="_blank" rel="noopener noreferrer">${url.value}</a>`,
      )
      .run()
  } else {
    ed.chain().focus().extendMarkRange('link').setLink({ href: url.value }).run()
  }
  closeLinkInput()
}

const handleLinkKeydown = (e: KeyboardEvent) => {
  if (e.key === 'Enter') {
    e.preventDefault()
    applyLink()
  }
  if (e.key === 'Escape') closeLinkInput()
}
</script>

<template>
  <div
    class="rounded-lg border border-gray-300 overflow-hidden focus-within:ring-1 focus-within:ring-csub-blue focus-within:border-csub-blue transition-shadow"
  >
    <div v-if="editor">
      <div
        class="flex flex-wrap items-center gap-0.5 px-2 py-1.5 border-b border-gray-200 bg-gray-50/50 rounded-t-lg"
      >
        <!-- Undo / Redo -->
        <button
          type="button"
          @click="editor.chain().focus().undo().run()"
          :disabled="!editor.can().undo()"
          title="Undo"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            !editor.can().undo()
              ? 'text-gray-300 cursor-not-allowed'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <svg
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M3 10h10a5 5 0 015 5v2M3 10l4-4M3 10l4 4"
            />
          </svg>
        </button>
        <button
          type="button"
          @click="editor.chain().focus().redo().run()"
          :disabled="!editor.can().redo()"
          title="Redo"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            !editor.can().redo()
              ? 'text-gray-300 cursor-not-allowed'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <svg
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M21 10H11a5 5 0 00-5 5v2M21 10l-4-4M21 10l-4 4"
            />
          </svg>
        </button>

        <div class="w-px h-5 bg-gray-300 mx-1" />

        <!-- Text formatting -->
        <button
          type="button"
          @click="editor.chain().focus().toggleBold().run()"
          title="Bold"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            editor.isActive('bold')
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <strong>B</strong>
        </button>
        <button
          type="button"
          @click="editor.chain().focus().toggleItalic().run()"
          title="Italic"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            editor.isActive('italic')
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <em>I</em>
        </button>
        <button
          type="button"
          @click="editor.chain().focus().toggleUnderline().run()"
          title="Underline"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            editor.isActive('underline')
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <span class="underline">U</span>
        </button>
        <button
          type="button"
          @click="editor.chain().focus().toggleStrike().run()"
          title="Strikethrough"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            editor.isActive('strike')
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <span class="line-through">S</span>
        </button>

        <div class="w-px h-5 bg-gray-300 mx-1" />

        <!-- Headings -->
        <button
          type="button"
          @click="editor.chain().focus().toggleHeading({ level: 2 }).run()"
          title="Heading 2"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            editor.isActive('heading', { level: 2 })
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          H2
        </button>
        <button
          type="button"
          @click="editor.chain().focus().toggleHeading({ level: 3 }).run()"
          title="Heading 3"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            editor.isActive('heading', { level: 3 })
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          H3
        </button>

        <div class="w-px h-5 bg-gray-300 mx-1" />

        <!-- Lists -->
        <button
          type="button"
          @click="editor.chain().focus().toggleBulletList().run()"
          title="Bullet List"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            editor.isActive('bulletList')
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <svg
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <circle cx="4" cy="6" r="1.5" fill="currentColor" stroke="none" />
            <circle cx="4" cy="12" r="1.5" fill="currentColor" stroke="none" />
            <circle cx="4" cy="18" r="1.5" fill="currentColor" stroke="none" />
            <path stroke-linecap="round" d="M9 6h11M9 12h11M9 18h11" />
          </svg>
        </button>
        <button
          type="button"
          @click="editor.chain().focus().toggleOrderedList().run()"
          title="Numbered List"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            editor.isActive('orderedList')
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <svg class="w-4 h-4" viewBox="0 0 24 24" fill="currentColor" stroke="none">
            <text x="1" y="8" font-size="7" font-weight="bold">1.</text>
            <text x="1" y="14.5" font-size="7" font-weight="bold">2.</text>
            <text x="1" y="21" font-size="7" font-weight="bold">3.</text>
            <rect x="12" y="5" width="10" height="1.5" rx="0.75" />
            <rect x="12" y="11.5" width="10" height="1.5" rx="0.75" />
            <rect x="12" y="18" width="10" height="1.5" rx="0.75" />
          </svg>
        </button>

        <div class="w-px h-5 bg-gray-300 mx-1" />

        <!-- Block elements -->
        <button
          type="button"
          @click="editor.chain().focus().toggleBlockquote().run()"
          title="Blockquote"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            editor.isActive('blockquote')
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <svg
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M8 10h.01M12 10h.01M16 10h.01M9 16H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-5l-5 5v-5z"
            />
          </svg>
        </button>
        <button
          type="button"
          @click="editor.chain().focus().setHorizontalRule().run()"
          title="Horizontal Rule"
          class="px-2 py-1 rounded text-sm font-body transition-colors text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark"
        >
          <svg
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path stroke-linecap="round" d="M3 12h18" />
          </svg>
        </button>

        <div class="w-px h-5 bg-gray-300 mx-1" />

        <!-- Link -->
        <button
          type="button"
          @click="openLinkInput"
          title="Insert Link"
          :class="`px-2 py-1 rounded text-sm font-body transition-colors ${
            showLinkInput || editor.isActive('link')
              ? 'bg-csub-blue text-white'
              : 'text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark'
          }`"
        >
          <svg
            class="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1"
            />
          </svg>
        </button>

        <button
          v-if="editor.isActive('link')"
          type="button"
          @click="editor.chain().focus().unsetLink().run()"
          title="Remove Link"
          class="px-2 py-1 rounded text-sm font-body transition-colors text-csub-gray hover:bg-gray-100 hover:text-csub-blue-dark"
        >
          <svg
            class="w-4 h-4 text-red-500"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            :stroke-width="2"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636"
            />
          </svg>
        </button>
      </div>
      <div
        v-if="showLinkInput"
        class="flex items-center gap-2 px-2 py-1.5 border-b border-gray-200 bg-blue-50/80"
      >
        <svg
          class="w-4 h-4 text-csub-blue flex-shrink-0"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          :stroke-width="2"
        >
          <path
            stroke-linecap="round"
            stroke-linejoin="round"
            d="M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1"
          />
        </svg>
        <input
          ref="linkInputRef"
          type="url"
          v-model="url"
          @keydown="handleLinkKeydown"
          placeholder="https://example.com"
          class="flex-1 px-2 py-1 rounded border border-gray-300 font-body text-sm focus:outline-none focus:ring-1 focus:ring-csub-blue"
        />
        <button
          type="button"
          @click="applyLink"
          class="px-3 py-1 bg-csub-blue text-white text-xs font-body font-semibold rounded hover:bg-csub-blue-dark transition-colors"
        >
          Apply
        </button>
        <button
          type="button"
          @click="closeLinkInput"
          class="px-2 py-1 text-xs font-body text-csub-gray hover:text-csub-blue-dark transition-colors"
        >
          Cancel
        </button>
      </div>
    </div>
    <EditorContent :editor="editor" />
  </div>
</template>
