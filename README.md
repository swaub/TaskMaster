# TaskMaster - Professional Task Management Application

TaskMaster is a production-ready .NET 8 WPF desktop application designed for efficient task and routine management with enterprise-grade architecture and performance.

## 🚀 Features

### Core Functionality
- **Task Management**: Create, edit, complete, and delete tasks with priorities (1-10)
- **Daily Routines**: Manage recurring daily tasks that automatically reset
- **Categories**: Organize tasks with built-in and custom categories
- **Due Dates**: Set and track task deadlines with intelligent notifications
- **Multiple Views**: List, Table, and Grid views for optimal productivity
- **Sorting & Filtering**: Advanced sorting by priority, due date, category, and completion status

### Enterprise Architecture
- **Advanced Logging**: Comprehensive logging with automatic rotation and structured output
- **Configuration Management**: Robust configuration system with validation
- **Advanced Search**: Fuzzy search, regex support, and intelligent filtering with caching
- **Performance Optimization**: Memory caching, background processing, and efficient data handling
- **Thread Safety**: Robust concurrent access handling with locks and semaphores
- **Error Recovery**: Bulletproof data persistence with atomic operations and backup system

### Technical Excellence
- **MVVM Pattern**: Clean separation of concerns with proper data binding
- **Memory Management**: Optimized for performance with proper resource disposal
- **Single Instance**: Prevents multiple application instances
- **Timezone Support**: Full timezone awareness with automatic handling
- **Input Validation**: Comprehensive validation with sanitization
- **Data Integrity**: Automatic backup and recovery systems

## 📋 System Requirements

- **Operating System**: Windows 10/11 (x64)
- **Framework**: .NET 8.0 (included in standalone executable)
- **Memory**: 512 MB RAM minimum, 1 GB recommended
- **Storage**: 100 MB available disk space

## 🛠️ Development Setup

### Prerequisites
- Visual Studio 2022 (17.8 or later) OR Visual Studio Code with C# extension
- .NET 8.0 SDK

### Setting up in Visual Studio
1. Clone the repository:
   ```bash
   git clone https://github.com/yourusername/TaskMaster.git
   ```
2. Open `TaskMaster.sln` in Visual Studio
3. Restore NuGet packages (automatic on first build)
4. Set `TaskMaster` as the startup project
5. Build and run with F5

### Setting up in Visual Studio Code
1. Clone the repository:
   ```bash
   git clone https://github.com/yourusername/TaskMaster.git
   cd TaskMaster
   ```
2. Open the folder in VS Code
3. Install recommended extensions (C# for Visual Studio Code)
4. Open integrated terminal and run:
   ```bash
   dotnet restore
   dotnet build
   dotnet run --project TaskMaster/TaskMaster.csproj
   ```

## 🔨 Building

### Debug Build
```bash
dotnet build
```

### Release Build
```bash
dotnet build --configuration Release
```

### Standalone Executable
```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
The standalone executable will be created at:
`TaskMaster/bin/Release/net8.0-windows/win-x64/publish/TaskMaster.exe`

## 📁 Project Structure

```
TaskMaster/
├── TaskMaster.sln              # Visual Studio solution file
├── README.md                   # Project documentation
├── CLAUDE.md                   # Development guidelines
└── TaskMaster/                 # Main project folder
    ├── TaskMaster.csproj       # Project configuration
    ├── App.xaml                # Application resources
    ├── App.xaml.cs             # Application startup logic
    ├── MainWindow.xaml         # Main UI definition
    ├── MainWindow.xaml.cs      # Main window code-behind
    ├── MainViewModel.cs        # MVVM ViewModel with business logic
    ├── Core.cs                 # Core models, utilities, and services
    ├── Services.cs             # Data persistence and notification services
    ├── Advanced.cs             # Advanced search and performance services
    ├── Converters.cs           # XAML value converters
    └── AssemblyInfo.cs         # Assembly metadata
```

## 🔧 Configuration

TaskMaster stores configuration and data in `%LOCALAPPDATA%/TaskMaster/`:
- `tasks.json` - Task data with automatic backup
- `routines.json` - Routine data with automatic backup
- `Config/` - Configuration files
- `Logs/` - Application logs with rotation

## 🎯 Usage

### Basic Operations
1. **Add Task**: Enter task name, description, priority (1-10), and optional due date
2. **Set Categories**: Use predefined categories or create custom ones
3. **Complete Tasks**: Check the checkbox to mark tasks complete
4. **Manage Routines**: Add daily routines that reset automatically each day
5. **Filter & Sort**: Use category filter and sorting options for organization

### Keyboard Shortcuts
- `Ctrl+N` - Focus new task name field
- `Ctrl+R` - Reset daily routines
- `F1` - Show help dialog
- `Tab` - Navigate between controls
- `Enter` - Activate focused control

## 🔍 Advanced Features

### Search System
- **Text Search**: Search across task names, descriptions, and categories
- **Fuzzy Matching**: Find tasks with approximate text matching
- **Regex Support**: Use regular expressions for complex searches
- **Caching**: Intelligent search result caching for performance

### Performance Features
- **Memory Caching**: Optimized data access with automatic cache management
- **Background Processing**: Non-blocking operations for UI responsiveness
- **Efficient Rendering**: Optimized for handling large numbers of tasks
- **Resource Management**: Proper disposal and memory leak prevention

## 📊 Dependencies

### Core Dependencies
- **.NET 8.0**: Target framework
- **Newtonsoft.Json**: Data serialization and deserialization
- **Microsoft.Extensions.Caching.Memory**: Performance optimization caching

### Project Architecture
- **MVVM Pattern**: Model-View-ViewModel architecture
- **WPF**: Windows Presentation Foundation for modern UI
- **Consolidated Design**: 8 focused C# files for maintainability

## 🛡️ Data Safety

- **Automatic Backups**: Tasks and routines backed up before saves
- **Atomic Operations**: File operations use temporary files and atomic moves
- **Error Recovery**: Robust error handling with graceful degradation
- **Data Validation**: Input validation and sanitization throughout
- **File Locking**: Prevents corruption from concurrent access

## 🚀 Performance

TaskMaster is optimized for:
- **Large Datasets**: Efficiently handles thousands of tasks
- **Memory Usage**: Optimized memory management with caching
- **Startup Time**: Fast application startup
- **UI Responsiveness**: Debounced operations prevent lag

## 🛠️ Troubleshooting

### Common Issues
1. **Build errors**: Ensure .NET 8.0 SDK is installed
2. **Application won't start**: Check Windows Event Viewer for errors
3. **Data not saving**: Verify write permissions to `%LOCALAPPDATA%`
4. **Performance issues**: Check available memory and disk space

### Development Debugging
- Set breakpoints in Visual Studio/VS Code
- Check Output window for build information
- Review logs in `%LOCALAPPDATA%/TaskMaster/Logs/`
- Use Debug configuration for detailed error information

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/amazing-feature`
3. Follow the coding standards in `CLAUDE.md`
4. Test your changes thoroughly
5. Commit changes: `git commit -m 'Add amazing feature'`
6. Push to branch: `git push origin feature/amazing-feature`
7. Open a Pull Request

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- Built with .NET 8 and WPF for modern Windows desktop development
- Uses Newtonsoft.Json for robust data serialization
- Microsoft.Extensions.Caching.Memory for enterprise-grade performance
- MVVM pattern for clean, maintainable architecture

## 📞 Support

For issues, questions, or feature requests:
- Create an issue on GitHub with detailed information
- Check the troubleshooting section above
- Review logs in `%LOCALAPPDATA%/TaskMaster/Logs/` for errors

---

**TaskMaster** - Professional task management for productive workflows.