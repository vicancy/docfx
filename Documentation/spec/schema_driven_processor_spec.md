# DocFX Schema-driven Document Processor Design Spec

## 1. Introduction
DocFX supports different [document processors](..\tutorial\howto_build_your_own_type_of_documentation_with_custom_plug-in.md) to handle different kinds of input. For now, if the data model changes a bit, a new document processor is needed, even most of the work in processors are the same.

This spec describes the document processor that leverages [DocFX Document Schema](docfx_document_schema.md) to interpret different kinds of input documents.

## 2. Goals and Non-Goals
### Goals

### Non-Goals

## 7. Samples
Here's an sample of the schema. Assume we have the following YAML file:
```yaml
### YamlMime:LandingPage
title: Web Apps Documentation
metadata:
  title: Azure Web Apps Documentation - Tutorials, API Reference
  meta.description: Learn how to use App Service Web Apps to build and host websites and web applications.
  services: app-service
  author: apexprodleads
  manager: carolz
  ms.service: app-service
  ms.tgt_pltfrm: na
  ms.devlang: na
  ms.topic: landing-page
  ms.date: 01/23/2017
  ms.author: carolz
sections:
- title: 5-Minute Quickstarts
  children:
  - text: .NET
    href: app-service-web-get-started-dotnet.md
  - text: Node.js
    href: app-service-web-get-started-nodejs.md
  - text: PHP
    href: app-service-web-get-started-php.md
  - text: Java
    href: app-service-web-get-started-java.md
  - text: Python
    href: app-service-web-get-started-python.md
  - text: HTML
    href: app-service-web-get-started-html.md
- title: Step-by-Step Tutorials
  children:
  - content: "Create an application using [.NET with Azure SQL DB](app-service-web-tutorial-dotnet-sqldatabase.md) or [Node.js with MongoDB](app-service-web-tutorial-nodejs-mongodb-app.md)"
  - content: "[Map an existing custom domain to your application](app-service-web-tutorial-custom-domain.md)"
  - content: "[Bind an existing SSL certificate to your application](app-service-web-tutorial-custom-SSL.md)"
```

In this sample, we want to use the JSON schema to describe the overall model structure. Further more, the `href` is a file link. It need to be resolved from the relative path to the final href. The `content` property need to be marked up as a Markdown string. The `metadata` need to be tagged for further custom operations. We want to use `section`'s `title` as the key for overwrite `section` array.

Here's the schema to describe these operations:

```json
{
    "$schema": "https://github.com/dotnet/docfx/schemas/v1.0/schema.json#",
    "version": "1.0.0",
    "id": "https://github.com/dotnet/docfx/schemas/landingpage.schema.json",
    "title": "LandingPage",
    "description": "The schema for landing page",
    "type": "object",
    "properties": {
        "metadata": {
            "type": "object",
            "tags": [ "metadata" ]
        },
        "sections": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "children": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "href": {
                                    "type": "string",
                                    "contentType": "href"
                                },
                                "text": {
                                    "type": "string",
                                    "tags": [ "localizable" ]
                                },
                                "content": {
                                    "type": "string",
                                    "contentType": "markdown"
                                }
                            }
                        }
                    },
                    "title": {
                        "type": "string",
                        "mergeType": "key"
                    }
                }
            }
        },
        "title": {
            "type": "string"
        }
    }
}
```

## 8. Open Issues
1. DocFX fills `_global` metadata into the processed data model, should the schema reflect this behavior?
   * If YES: 
        * Pros:
            1. Users are aware of the existence of `_global` metadata, they can overwrite the property if they want.
            2. Template writers are aware of it, they can completely rely on the schema to write the template.
        * Cons:
            1. Schema writers need aware of the existence of `_global` metadata, it should always exists for any schema. (Should we introduce in a concept of base schema?)
    * Decision: *NOT* include, this schema is for **general purpose**, use documents to describe the changes introduced by DocFX.
2. Is it necessary to prefix `d-` to every field that DocFX introduces in?
    * If keep `d-`
        * Pros:
            1. `d-` makes it straightforward that these keywords are introduced by DocFX
            2. Keywords DocFX introduces in will never duplicate with the one preserved by JSON schema
        * Cons:
            1. `d-` prefix provides a hint that these keywords are not *first class* keywords
            2. Little chance that keywords DocFX defines duplicate with what JSON schema defines, after all, JSON schema defines a finite set of reserved keywords.
            3. For example[Swagger spec](http://swagger.io/) is also based on JSON schema and the fields it introduces in has no prefix. 
    * Decision: *Remove* `d-` prefix.
