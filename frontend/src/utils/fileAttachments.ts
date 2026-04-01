import type { IFileAttachment } from '../types/chat';

export interface FileConversionResult {
  name: string;
  dataUri: string;
  mimeType: string;
  sizeBytes: number;
}

export interface FileValidationResult {
  valid: boolean;
  error?: string;
}

const MAX_FILE_SIZE = 5 * 1024 * 1024; // 5MB for images
const MAX_DOCUMENT_SIZE = 20 * 1024 * 1024; // 20MB for documents
const MAX_FILE_COUNT = 10; // Total attachments (images + documents)
const ALLOWED_IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/jpg', 'image/gif', 'image/webp'];

// XLSX is supported by backend preprocessing and inlined as text.
// Other Office formats remain unsupported.
const ALLOWED_DOCUMENT_TYPES = [
  'application/pdf',
  'text/plain',
  'text/markdown',
  'text/csv',
  'application/json',
  'text/html',
  'application/xml',
  'text/xml',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  'application/vnd.ms-excel',
];

// Extension to MIME type mapping for files where browser doesn't provide correct MIME type
const EXTENSION_TO_MIME: Record<string, string> = {
  '.md': 'text/markdown',
  '.markdown': 'text/markdown',
  '.txt': 'text/plain',
  '.csv': 'text/csv',
  '.json': 'application/json',
  '.html': 'text/html',
  '.htm': 'text/html',
  '.xml': 'application/xml',
  '.pdf': 'application/pdf',
  '.xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  '.xls': 'application/vnd.ms-excel',
};

/**
 * Get the effective MIME type for a file, falling back to extension-based detection.
 * Browsers often report empty or generic MIME types for certain file extensions.
 * 
 * @param file - File to get MIME type for
 * @returns The MIME type string
 */
export function getEffectiveMimeType(file: File): string {
  // If browser provides a specific MIME type, use it
  if (file.type && file.type !== 'application/octet-stream') {
    return file.type.toLowerCase();
  }
  
  // Fall back to extension-based detection
  const extension = file.name.toLowerCase().match(/\.[^.]+$/)?.[0] || '';
  return EXTENSION_TO_MIME[extension] || file.type || '';
}

/**
 * Validate if a file is a supported image type and within size limits.
 * 
 * @param file - File to validate
 * @returns Validation result with error message if invalid
 */
export function validateImageFile(file: File): FileValidationResult {
  const mimeType = getEffectiveMimeType(file);
  
  if (!mimeType.startsWith('image/')) {
    return { valid: false, error: `"${file.name}" is not an image file` };
  }

  if (!ALLOWED_IMAGE_TYPES.includes(mimeType)) {
    return { valid: false, error: `"${file.name}" format not supported. Use PNG, JPEG, GIF, or WebP` };
  }

  if (file.size > MAX_FILE_SIZE) {
    const sizeMB = (file.size / (1024 * 1024)).toFixed(1);
    return { valid: false, error: `"${file.name}" is ${sizeMB}MB. Maximum file size is 5MB` };
  }

  return { valid: true };
}

/**
 * Validate if a file is a supported document type and within size limits.
 * 
 * @param file - File to validate
 * @returns Validation result with error message if invalid
 */
export function validateDocumentFile(file: File): FileValidationResult {
  const mimeType = getEffectiveMimeType(file);
  
  if (!ALLOWED_DOCUMENT_TYPES.includes(mimeType)) {
    return { 
      valid: false, 
      error: `"${file.name}" format not supported. Use PDF, TXT, MD, CSV, JSON, HTML, XML, or XLSX` 
    };
  }

  if (file.size > MAX_DOCUMENT_SIZE) {
    const sizeMB = (file.size / (1024 * 1024)).toFixed(1);
    return { valid: false, error: `"${file.name}" is ${sizeMB}MB. Maximum file size is 20MB` };
  }

  return { valid: true };
}

/**
 * Validate any file (image or document) based on its type.
 * 
 * @param file - File to validate
 * @returns Validation result with error message if invalid
 */
export function validateFile(file: File): FileValidationResult {
  const mimeType = getEffectiveMimeType(file);
  
  if (mimeType.startsWith('image/')) {
    return validateImageFile(file);
  } else if (ALLOWED_DOCUMENT_TYPES.includes(mimeType)) {
    return validateDocumentFile(file);
  } else {
    return { 
      valid: false, 
      error: `"${file.name}" is not a supported file type` 
    };
  }
}

/**
 * Validate multiple files for count and individual file requirements.
 * 
 * @param files - Files to validate
 * @param currentFileCount - Number of files already attached
 * @returns Validation result with error message if invalid
 */
export function validateFileCount(files: File[], currentFileCount: number = 0): FileValidationResult {
  const totalCount = currentFileCount + files.length;
  
  if (totalCount > MAX_FILE_COUNT) {
    return { 
      valid: false, 
      error: `Maximum ${MAX_FILE_COUNT} files allowed. You have ${currentFileCount} attached and are trying to add ${files.length} more` 
    };
  }

  return { valid: true };
}

/**
 * Convert a single file to base64 data URI with correct MIME type.
 * The browser's FileReader may embed incorrect MIME types, so we rebuild
 * the data URI with the effective MIME type.
 * 
 * @param file - File to convert
 * @param effectiveMimeType - The correct MIME type to use
 * @returns Promise resolving to data URI string with correct MIME type
 * @throws {Error} If file reading fails
 */
async function convertFileToDataUri(file: File, effectiveMimeType: string): Promise<string> {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result as string;
      // The data URI format is: data:[<mediatype>][;base64],<data>
      // We need to replace the MIME type portion with the correct one
      const commaIndex = result.indexOf(',');
      if (commaIndex === -1) {
        reject(new Error('Invalid data URI format'));
        return;
      }
      const base64Data = result.substring(commaIndex + 1);
      const correctedDataUri = `data:${effectiveMimeType};base64,${base64Data}`;
      resolve(correctedDataUri);
    };
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

/**
 * Convert multiple files to base64 data URIs with metadata.
 * Supports both images and documents.
 * 
 * @param files - Array of File objects to convert
 * @returns Array of conversion results with file metadata
 * @throws {Error} If any file is invalid or conversion fails
 */
export async function convertFilesToDataUris(
  files: File[]
): Promise<FileConversionResult[]> {
  const results: FileConversionResult[] = [];

  for (const file of files) {
    // Validate each file before conversion
    const validation = validateFile(file);
    if (!validation.valid) {
      throw new Error(validation.error);
    }

    const effectiveMimeType = getEffectiveMimeType(file);
    const dataUri = await convertFileToDataUri(file, effectiveMimeType);

    results.push({
      name: file.name,
      dataUri,
      mimeType: effectiveMimeType,
      sizeBytes: file.size,
    });
  }

  return results;
}

/**
 * Create chat attachment metadata from file conversion results.
 * 
 * @param results - File conversion results
 * @returns Array of attachment objects for chat UI
 */
export function createAttachmentMetadata(
  results: FileConversionResult[]
): IFileAttachment[] {
  return results.map((result) => ({
    fileName: result.name,
    fileSizeBytes: result.sizeBytes,
    dataUri: result.dataUri,
  }));
}
