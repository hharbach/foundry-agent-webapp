import type { AccountInfo } from '@azure/msal-browser';
import type { IChatItem, IUsageInfo, IAnnotation, IMcpApprovalRequest } from './chat';
import type { AppError } from './errors';

// Re-export types for convenience
export type { IChatItem, IUsageInfo, IAnnotation, IMcpApprovalRequest };

export interface ConversationSummary {
  id: string;
  title: string | null;
  createdAt: number;
}

export interface ConversationMessageInfo {
  role: string;
  content: string;
}

/**
 * Central application state structure
 * All application state flows through this single source of truth
 */
export interface AppState {
  // Authentication state
  auth: {
    status: 'initializing' | 'authenticated' | 'unauthenticated' | 'error';
    user: AccountInfo | null;
    error: string | null;
  };
  
  // Chat operations state
  chat: {
    status: 'idle' | 'sending' | 'streaming' | 'error';
    messages: IChatItem[];
    currentConversationId: string | null;
    error: AppError | null;
    streamingMessageId?: string;
    recoveredInput?: string;
    pendingMessages: string[]; // queued messages waiting to send after current stream completes
  };

  // Conversation history state
  conversations: {
    list: ConversationSummary[];
    isLoading: boolean;
    sidebarOpen: boolean;
    hasMore: boolean;
  };
  
  // UI coordination state
  ui: {
    chatInputEnabled: boolean; // Disable during streaming/errors
  };
}

/**
 * All possible actions that can modify application state
 * Use discriminated unions for type safety
 */
export type AppAction = 
  // Auth actions
  | { type: 'AUTH_INITIALIZED'; user: AccountInfo }
  | { type: 'AUTH_TOKEN_EXPIRED' }
  
  // Chat actions
  | { type: 'CHAT_SEND_MESSAGE'; message: IChatItem }
  | { type: 'CHAT_LOAD_MESSAGES'; messages: IChatItem[] }
  | { type: 'CHAT_START_STREAM'; conversationId?: string; messageId: string }
  | { type: 'CHAT_STREAM_CHUNK'; messageId: string; content: string }
  | { type: 'CHAT_STREAM_ANNOTATIONS'; messageId: string; annotations: IAnnotation[] }
  | { type: 'CHAT_MCP_APPROVAL_REQUEST'; messageId: string; approvalRequest: IMcpApprovalRequest; previousResponseId: string | null }
  | { type: 'CHAT_MCP_APPROVAL_RESOLVED'; approvalRequestId: string; resolved?: 'approved' | 'rejected' }
  | { type: 'CHAT_STREAM_COMPLETE'; usage: IUsageInfo }
  | { type: 'CHAT_CANCEL_STREAM' }
  | { type: 'CHAT_ERROR'; error: AppError } // Enhanced error object
  | { type: 'CHAT_CLEAR_ERROR' } // Clear error state
  | { type: 'CHAT_CLEAR' }
  | { type: 'CHAT_ADD_ASSISTANT_MESSAGE'; messageId: string }
  | { type: 'CHAT_LOAD_CONVERSATION'; conversationId: string; messages: IChatItem[] }
  | { type: 'CHAT_STREAM_RETRY'; messageId: string; attempt: number; maxRetries: number }
  | { type: 'CHAT_RECOVER_MESSAGE'; messageText: string; error: AppError; retryCount: number }
  | { type: 'CHAT_CONSUMED_RECOVERED_INPUT' }
  | { type: 'CHAT_QUEUE_MESSAGE'; text: string }
  | { type: 'CHAT_DEQUEUE_MESSAGE'; index: number }
  | { type: 'CHAT_CLEAR_QUEUE' }

  // Conversation history actions
  | { type: 'CONVERSATIONS_SET_LIST'; conversations: ConversationSummary[]; hasMore: boolean; append?: boolean }
  | { type: 'CONVERSATIONS_LOADING' }
  | { type: 'CONVERSATIONS_LOADING_DONE' }
  | { type: 'CONVERSATIONS_TOGGLE_SIDEBAR' }
  | { type: 'CONVERSATIONS_REMOVE'; conversationId: string };

/**
 * Initial state for the application
 */
export const initialAppState: AppState = {
  auth: {
    status: 'initializing',
    user: null,
    error: null,
  },
  chat: {
    status: 'idle',
    messages: [],
    currentConversationId: null,
    error: null,
    streamingMessageId: undefined,
    recoveredInput: undefined,
    pendingMessages: [],
  },
  conversations: {
    list: [],
    isLoading: false,
    sidebarOpen: false,
    hasMore: false,
  },
  ui: {
    chatInputEnabled: true,
  },
};
