# CUE4Parse Wiki Documentation - Additional Pages

This directory contains comprehensive documentation for CUE4Parse topics that were planned but never created in the official wiki.

## Created Documentation

The following pages have been created based on the CUE4Parse repository code, examples, and best practices:

### 📚 Core Topics

1. **[Getting-Started.md](Getting-Started.md)** - Complete beginner's guide to using CUE4Parse
   - Creating file providers
   - Loading packages and objects
   - Basic workflow examples
   - Working with different asset types

2. **[Version-Containers.md](Version-Containers.md)** - Understanding Unreal Engine versions
   - EGame version enumeration
   - Texture platform configuration
   - Version detection strategies
   - Best practices for version matching

3. **[AES-Encryption-Keys.md](AES-Encryption-Keys.md)** - Working with encrypted archives
   - Key format and submission
   - Multiple key management
   - Secure key storage practices
   - Troubleshooting encryption issues

4. **[Mounting-Archives.md](Mounting-Archives.md)** - Archive mounting explained
   - Automatic vs manual mounting
   - Multiple directory support
   - PostMount for encrypted content
   - Virtual path mapping

5. **[Exportable-Objects.md](Exportable-Objects.md)** - Exporting assets to common formats
   - Texture export (PNG, TGA, DDS)
   - Sound export (OGG, MP3, WAV, etc.)
   - Mesh export (glTF, UEFormat, ActorX)
   - Animation export
   - Batch processing examples

6. **[Mapping-Files.md](Mapping-Files.md)** - Using .usmap property mappings
   - What mapping files are and why they're needed
   - Loading and using mappings
   - Finding mapping files for games
   - Version matching importance
   - Creating custom mappings

## Using These Documents

### For the CUE4Parse Wiki

These markdown files are formatted to be compatible with GitHub wikis. To contribute them to the official CUE4Parse wiki:

1. Clone the wiki repository:
   ```bash
   git clone https://github.com/FabianFG/CUE4Parse.wiki.git
   ```

2. Copy the desired `.md` files to the wiki repository

3. Update the `Home.md` and `_Sidebar.md` to link to the new pages

4. Commit and push your changes

### For Your Own Projects

Use these as reference documentation or include them in your project's documentation folder.

## Topics Still Needing Documentation

The following topics from the original wiki outline still need to be created:

- **Terminology** - Glossary of CUE4Parse and Unreal Engine terms
- **FAQ** - Common questions and answers
- **Archive Readers** - Low-level archive reading details
- **Virtual Paths** - Understanding CUE4Parse path system
- **Package Localization** - Working with localized string tables
- **Custom Object Type** - Creating custom asset type handlers
- **Common Errors** - Troubleshooting guide
- **Coding Style** - Contribution guidelines for code style
- **Contributing** - How to contribute to CUE4Parse

## Sources

These documents were created by analyzing:
- CUE4Parse source code repository
- Example projects in the repository
- Community best practices
- Related Unreal Engine documentation

## Contributing

Feel free to:
- Improve existing documentation
- Add missing topics
- Fix errors or unclear explanations
- Add more examples
- Update for new CUE4Parse versions

## License

These documentation files are provided for the CUE4Parse community under the same license as CUE4Parse itself (Apache License 2.0).
