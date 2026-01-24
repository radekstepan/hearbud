#!/usr/bin/env node

const fs = require('fs');
const path = require('path');

// Input and output file paths
const inputFile = path.join(__dirname, 'review.json');
const outputFile = path.join(__dirname, 'code-review.md');

try {
  // Read the JSON file
  const jsonData = fs.readFileSync(inputFile, 'utf8');
  const data = JSON.parse(jsonData);

  // Extract text content (skip thinking blocks)
  const textContent = data.content
    .filter(item => item.type === 'text')
    .map(item => item.text)
    .join('\n\n');

  if (!textContent) {
    console.error('No text content found in the JSON file.');
    process.exit(1);
  }

  // Write to markdown file
  fs.writeFileSync(outputFile, textContent, 'utf8');
  
  console.log(`✓ Successfully extracted review to: ${outputFile}`);
  console.log(`  Content length: ${textContent.length} characters`);
  
} catch (error) {
  console.error('Error processing file:', error.message);
  process.exit(1);
}
