# DocFX Schema Driven Processor (SDP) Design Spec

## 1. Introduction
DocFX supports different [document processors](..\tutorial\howto_build_your_own_type_of_documentation_with_custom_plug-in.md) to handle different kinds of input. For now, if the data model changes a bit, a new document processor is needed, even most of the work in processors are the same.

This spec describes the how to validate and interpret the given document based on [DocFX Document Schema](docfx_document_schema.md). 

## 2. Goals and Non-goals

### 2.1 Goals
1. How to load schema and how to parse it
2. How to run schema against the document

### 2.2 Non-goals
1. Custom tag interpret such as 'localizable'

## 3. Tasks
1. How to expand the referenced model with 'uid'
2. How to register XrefSpec with 'uidLink'
3. How to access the referenced model from template
4. How to incremental build without 'FillReference'
5. How to apply overwrite document
6. How and when to register custom tags

## 4. Principles
1. One input file to one output webpage
2. Unlike MREF, input model is similar to output model
    Gap: 1. Namespace page, how to feed in class summary
    
    
Propose 1. Schema to define an "Expand" behavior to include other models
    Cons: The output model is way different from the input schema, type of the property is changed
    Pros: Can fit into current template system
Propose 2. Provide a lookup method for Razor template to look up the model if it needed
    Cons: Incremental dependencies are hard to track; Dependent on the powerful template syntax, not compatible with mustache one


