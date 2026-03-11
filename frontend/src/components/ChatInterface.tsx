import { useRef, useEffect, useState } from "react";
import { AssistantMessage } from "./chat/AssistantMessage";
import { UserMessage } from "./chat/UserMessage";
import { McpApprovalCard } from "./chat/McpApprovalCard";
import { StarterMessages } from "./chat/StarterMessages";
import { ChatInput } from "./chat/ChatInput";
import { Waves } from "./animations/Waves";
import { ErrorMessage } from "./core/ErrorMessage";
import { BuiltWithBadge } from "./core/BuiltWithBadge";
import type { IChatItem } from "../types/chat";
import type { AppState } from "../types/appState";
import type { AppError } from "../types/errors";
import styles from './ChatInterface.module.css';

interface ChatInterfaceProps {
  messages: IChatItem[];
  status: AppState['chat']['status'];
  error: AppError | null;
  streamingMessageId?: string;
  recoveredInput?: string;
  pendingMessages?: string[];
  onSendMessage: (text: string, files?: File[]) => void;
  onMcpApproval?: (approvalRequestId: string, approved: boolean, previousResponseId: string, conversationId: string) => void;
  onClearError?: () => void;
  onRecoveredInputConsumed?: () => void;
  onDequeueMessage?: (index: number) => void;
  onOpenSettings?: () => void;
  onNewChat?: () => void;
  onCancelStream?: () => void;
  onToggleSidebar?: () => void;
  hasMessages?: boolean;
  disabled: boolean;
  agentName?: string;
  agentDescription?: string;
  agentLogo?: string;
  starterPrompts?: string[];
  conversationId?: string | null;
}

export const ChatInterface: React.FC<ChatInterfaceProps> = (props) => {
  const { messages, status, error, streamingMessageId, recoveredInput, pendingMessages, onSendMessage, onMcpApproval, onClearError, onRecoveredInputConsumed, onDequeueMessage, onOpenSettings, onNewChat, onCancelStream, onToggleSidebar, hasMessages, disabled, agentName, agentDescription, agentLogo, starterPrompts, conversationId } = props;
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const [liveRegionMessage, setLiveRegionMessage] = useState<string>('');
  
  const isStreaming = status === 'streaming';
  const isBusy = disabled || status === 'sending';

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  };

  useEffect(() => {
    // Scroll immediately on every message change for real-time streaming feel
    scrollToBottom();
  }, [messages]);

  useEffect(() => {
    if (isStreaming) {
      const streamingMessage = messages.find(m => m.id === streamingMessageId);
      if (streamingMessage?.retryAttempt) {
        setLiveRegionMessage(`Retrying, attempt ${streamingMessage.retryAttempt} of ${streamingMessage.maxRetries}`);
      } else {
        setLiveRegionMessage('Assistant is responding');
      }
    } else if (status === 'idle' && messages.length > 0 && messages[messages.length - 1].role === 'assistant') {
      setLiveRegionMessage('Response complete');
      const timer = setTimeout(() => setLiveRegionMessage(''), 1000);
      return () => clearTimeout(timer);
    }
  }, [isStreaming, status, messages, streamingMessageId]);

  const handleSendMessage = (messageText: string, files?: File[]) => {
    if (!messageText.trim() || disabled) return;
    onSendMessage(messageText, files);
  };

  const handleStarterPromptClick = (prompt: string) => {
    handleSendMessage(prompt);
  };

  return (
    <div className={styles.chatContainer}>
      {/* Live region for announcing streaming status to screen readers */}
      <div 
        role="status" 
        aria-live="polite" 
        aria-atomic="true"
        className="sr-only"
      >
        {liveRegionMessage}
      </div>

      <div 
        className={styles.messagesContainer} 
        role="log" 
        aria-live="polite" 
        aria-label="Chat messages"
        aria-busy={isStreaming}
      >
        <div className={styles.messagesWrapper}>
          {messages.length === 0 ? (
            <StarterMessages 
              agentName={agentName}
              agentDescription={agentDescription}
              agentLogo={agentLogo}
              starterPrompts={starterPrompts}
              onPromptClick={handleStarterPromptClick}
            />
          ) : (
            <>
              <div aria-live="polite" aria-atomic="false" className="sr-only">
                {messages.length > 0 && messages[messages.length - 1].role === 'assistant' && 
                  `Assistant: ${messages[messages.length - 1].content.substring(0, 100)}`
                }
              </div>
              {messages.map((message) =>
                message.role === "approval" ? (
                  <McpApprovalCard
                    key={message.id}
                    toolName={message.mcpApproval?.toolName || ''}
                    serverLabel={message.mcpApproval?.serverLabel || ''}
                    arguments={message.mcpApproval?.arguments}
                    resolved={message.mcpApproval?.resolved}
                    onApprove={() => onMcpApproval?.(
                      message.mcpApproval!.id,
                      true,
                      message.mcpApproval!.previousResponseId || '',
                      conversationId || ''
                    )}
                    onReject={() => onMcpApproval?.(
                      message.mcpApproval!.id,
                      false,
                      message.mcpApproval!.previousResponseId || '',
                      conversationId || ''
                    )}
                    disabled={isBusy}
                    agentName={agentName}
                    agentLogo={agentLogo}
                  />
                ) : message.role === "user" ? (
                  <UserMessage key={message.id} message={message} />
                ) : (
                  <AssistantMessage 
                    key={message.id} 
                    message={message} 
                    isStreaming={isStreaming && message.id === streamingMessageId}
                    agentName={agentName}
                    agentLogo={agentLogo}
                  />
                )
              )}
              <div ref={messagesEndRef} />
            </>
          )}
        </div>
      </div>

      <div className={styles.chatInputArea}>
        {error && (
          <div className={styles.errorWrapper}>
            <ErrorMessage
              message={typeof error.message === 'string' ? error.message : 
                      typeof error === 'string' ? error :
                      error.originalError?.message || 
                      'An unexpected error occurred. Please try again.'}
              recoverable={error.recoverable}
              onRetry={error.action?.handler}
              onDismiss={onClearError}
              customAction={error.action && error.action.label !== 'Retry' ? {
                label: error.action.label,
                handler: error.action.handler
              } : undefined}
            />
          </div>
        )}

        <Waves />
        <ChatInput
          onSubmit={handleSendMessage}
          disabled={isBusy}
          onOpenSettings={onOpenSettings}
          onNewChat={onNewChat}
          onToggleSidebar={onToggleSidebar}
          hasMessages={hasMessages}
          placeholder="Type your message here..."
          isStreaming={isStreaming}
          onCancelStream={isStreaming && onCancelStream ? onCancelStream : undefined}
          recoveredInput={recoveredInput}
          onRecoveredInputConsumed={onRecoveredInputConsumed}
          pendingMessages={pendingMessages}
          onDequeueMessage={onDequeueMessage}
        />
        <BuiltWithBadge className={styles.builtWithBadge} />
      </div>
    </div>
  );
};
