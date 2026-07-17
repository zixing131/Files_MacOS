#import <AppKit/AppKit.h>
#import <CoreServices/CoreServices.h>
#import <CoreText/CoreText.h>
#import <NetFS/NetFS.h>
#import <QuickLook/QuickLook.h>
#import <QuickLookUI/QuickLookUI.h>
#import <QuickLookThumbnailing/QuickLookThumbnailing.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#import <dispatch/dispatch.h>
#import <copyfile.h>
#import <errno.h>
#import <grp.h>
#import <limits.h>
#import <math.h>
#import <pwd.h>
#import <stdatomic.h>
#import <stdlib.h>
#import <string.h>
#import <sys/stat.h>
#import <sys/acl.h>
#import <sys/xattr.h>

static const char *FilesTrashOriginalPathAttribute = "io.filesmacos.original-path";

@interface FilesQuickLookDataSource : NSObject <QLPreviewPanelDataSource>
@property(nonatomic, strong) NSURL *url;
@end

@implementation FilesQuickLookDataSource

- (NSInteger)numberOfPreviewItemsInPreviewPanel:(QLPreviewPanel *)panel
{
	return self.url == nil ? 0 : 1;
}

- (id<QLPreviewItem>)previewPanel:(QLPreviewPanel *)panel previewItemAtIndex:(NSInteger)index
{
	return self.url;
}

@end

typedef void (*FilesMenuCommandCallback)(void *context, int command);
typedef int (*FilesMenuValidationCallback)(void *context, int command);
typedef void (*FilesAuxiliaryMouseCallback)(void *context, int buttonNumber);
typedef int (*FilesScrollWheelCallback)(void *context, double deltaX, double deltaY, int hasPreciseDeltas);

@interface FilesMenuTarget : NSObject <NSMenuItemValidation>
@property(nonatomic, assign) FilesMenuCommandCallback executeCallback;
@property(nonatomic, assign) FilesMenuValidationCallback validateCallback;
@property(nonatomic, assign) void *callbackContext;
- (void)executeCommand:(NSMenuItem *)sender;
@end

@implementation FilesMenuTarget

- (void)executeCommand:(NSMenuItem *)sender
{
	if (self.executeCallback != NULL)
	{
		self.executeCallback(self.callbackContext, (int)sender.tag);
	}
}

- (BOOL)validateMenuItem:(NSMenuItem *)menuItem
{
	return self.validateCallback == NULL || self.validateCallback(self.callbackContext, (int)menuItem.tag) != 0;
}

@end

static void files_macos_finish_file_drag(void);

@interface FilesFileDraggingSource : NSObject <NSDraggingSource>
@end

@implementation FilesFileDraggingSource

- (NSDragOperation)draggingSession:(NSDraggingSession *)session sourceOperationMaskForDraggingContext:(NSDraggingContext)context
{
	return NSDragOperationCopy;
}

- (BOOL)ignoreModifierKeysForDraggingSession:(NSDraggingSession *)session
{
	return YES;
}

- (void)draggingSession:(NSDraggingSession *)session endedAtPoint:(NSPoint)screenPoint operation:(NSDragOperation)operation
{
	files_macos_finish_file_drag();
}

@end

static FilesQuickLookDataSource *quickLookDataSource;
static NSSharingServicePicker *sharingServicePicker;
static FilesMenuTarget *mainMenuTarget;
static FilesFileDraggingSource *fileDraggingSource;
static NSArray<NSString *> *pendingFileDragPaths;
static NSWindow *pendingFileDragWindow;
static NSWindow *activeFileDragWindow;
static id fileDragEventMonitor;

static void files_macos_finish_file_drag(void)
{
	NSWindow *window = activeFileDragWindow;
	activeFileDragWindow = nil;
	dispatch_async(dispatch_get_main_queue(), ^{
		[NSCursor.arrowCursor set];
		if (window == nil)
		{
			return;
		}

		NSPoint location = [window convertPointFromScreen:NSEvent.mouseLocation];
		NSEvent *mouseUp = [NSEvent mouseEventWithType:NSEventTypeLeftMouseUp
			location:location
			modifierFlags:0
			timestamp:NSProcessInfo.processInfo.systemUptime
			windowNumber:window.windowNumber
			context:nil
			eventNumber:0
			clickCount:1
			pressure:0];
		[NSApp postEvent:mouseUp atStart:YES];
		[window invalidateCursorRectsForView:window.contentView];
	});
}

static void files_macos_clear_pending_file_drag(void)
{
	if (fileDragEventMonitor != nil)
	{
		[NSEvent removeMonitor:fileDragEventMonitor];
		fileDragEventMonitor = nil;
	}
	pendingFileDragPaths = nil;
	pendingFileDragWindow = nil;
}

static BOOL files_macos_begin_pending_file_drag(NSEvent *event)
{
	NSWindow *window = pendingFileDragWindow;
	NSView *view = window.contentView;
	NSArray<NSString *> *paths = pendingFileDragPaths;
	if (event == nil || window == nil || view == nil || paths.count == 0)
	{
		files_macos_clear_pending_file_drag();
		return NO;
	}

	NSMutableArray<NSDraggingItem *> *items = [NSMutableArray arrayWithCapacity:paths.count];
	NSPoint location = [view convertPoint:event.locationInWindow fromView:nil];
	NSInteger index = 0;
	for (NSString *path in paths)
	{
		NSURL *url = [NSURL fileURLWithPath:path];
		if (![NSFileManager.defaultManager fileExistsAtPath:url.path])
		{
			continue;
		}

		NSDraggingItem *item = [[NSDraggingItem alloc] initWithPasteboardWriter:url];
		NSImage *icon = [NSWorkspace.sharedWorkspace iconForFile:path];
		icon.size = NSMakeSize(64, 64);
		CGFloat offset = MIN(index, 4) * 4;
		[item setDraggingFrame:NSMakeRect(location.x - 32 + offset, location.y - 32 - offset, 64, 64) contents:icon];
		[items addObject:item];
		index++;
	}

	files_macos_clear_pending_file_drag();
	if (items.count == 0)
	{
		return NO;
	}
	if (fileDraggingSource == nil)
	{
		fileDraggingSource = [FilesFileDraggingSource new];
	}

	activeFileDragWindow = window;
	NSDraggingSession *session = [view beginDraggingSessionWithItems:items event:event source:fileDraggingSource];
	if (session == nil)
	{
		files_macos_finish_file_drag();
		return NO;
	}
	session.animatesToStartingPositionsOnCancelOrFail = YES;
	return YES;
}

__attribute__((visibility("default"))) int files_macos_prepare_file_drag(const char *pathsJson)
{
	@autoreleasepool
	{
		files_macos_clear_pending_file_drag();
		if (pathsJson == NULL)
		{
			return 0;
		}

		NSData *jsonData = [[NSData alloc] initWithBytes:pathsJson length:strlen(pathsJson)];
		id value = [NSJSONSerialization JSONObjectWithData:jsonData options:0 error:nil];
		if (![value isKindOfClass:NSArray.class])
		{
			return 0;
		}

		NSMutableArray<NSString *> *paths = [NSMutableArray array];
		for (id path in (NSArray *)value)
		{
			if ([path isKindOfClass:NSString.class] && [NSFileManager.defaultManager fileExistsAtPath:path])
			{
				[paths addObject:path];
			}
		}
		NSWindow *window = NSApp.keyWindow ?: NSApp.mainWindow;
		if (paths.count == 0 || window == nil)
		{
			return 0;
		}

		pendingFileDragPaths = [paths copy];
		pendingFileDragWindow = window;
		fileDragEventMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskLeftMouseDragged | NSEventMaskLeftMouseUp handler:^NSEvent *(NSEvent *event) {
			if (event.type == NSEventTypeLeftMouseUp)
			{
				files_macos_clear_pending_file_drag();
				return event;
			}

			NSWindow *sourceWindow = pendingFileDragWindow;
			NSPoint screenPoint = event.window == nil
				? NSEvent.mouseLocation
				: [event.window convertPointToScreen:event.locationInWindow];
			NSRect internalDragFrame = sourceWindow == nil ? NSZeroRect : NSInsetRect(sourceWindow.frame, 8, 8);
			if (sourceWindow != nil && NSPointInRect(screenPoint, internalDragFrame))
			{
				return event;
			}

			files_macos_begin_pending_file_drag(event);
			return nil;
		}];
		return fileDragEventMonitor == nil ? 0 : 1;
	}
}

static BOOL files_macos_symbol_font_has_required_glyphs(void)
{
	CTFontRef font = CTFontCreateWithName(CFSTR("Symbols"), 16, NULL);
	if (font == NULL)
	{
		return NO;
	}

	UniChar characters[] = { 0xE001, 0xE70D };
	CGGlyph glyphs[2] = { 0, 0 };
	BOOL hasGlyphs = CTFontGetGlyphsForCharacters(font, characters, glyphs, 2) && glyphs[0] != 0 && glyphs[1] != 0;
	CFRelease(font);
	return hasGlyphs;
}

__attribute__((visibility("default"))) int files_macos_register_symbol_font(void)
{
	@autoreleasepool
	{
		NSMutableArray<NSURL *> *candidates = [NSMutableArray array];
		NSURL *resourceURL = NSBundle.mainBundle.resourceURL;
		if (resourceURL != nil)
		{
			[candidates addObject:[resourceURL URLByAppendingPathComponent:@"Runtime/Uno.Fonts.Fluent/Fonts/uno-fluentui-assets.ttf"]];
		}

		NSString *executablePath = NSProcessInfo.processInfo.arguments.firstObject;
		if (executablePath.length > 0)
		{
			NSURL *executableDirectory = [[NSURL fileURLWithPath:executablePath] URLByDeletingLastPathComponent];
			[candidates addObject:[executableDirectory URLByAppendingPathComponent:@"Uno.Fonts.Fluent/Fonts/uno-fluentui-assets.ttf"]];
		}

		for (NSURL *url in candidates)
		{
			if (![NSFileManager.defaultManager fileExistsAtPath:url.path])
			{
				continue;
			}

			CFErrorRef error = NULL;
			BOOL registered = CTFontManagerRegisterFontsForURL(
				(__bridge CFURLRef)url,
				kCTFontManagerScopeProcess,
				&error);
			BOOL alreadyRegistered = error != NULL && CFErrorGetCode(error) == kCTFontManagerErrorAlreadyRegistered;
			if (error != NULL)
			{
				CFRelease(error);
			}
			if ((registered || alreadyRegistered) && files_macos_symbol_font_has_required_glyphs())
			{
				return 1;
			}
		}

		return files_macos_symbol_font_has_required_glyphs() ? 1 : 0;
	}
}

static id auxiliaryMouseMonitor;
static FilesAuxiliaryMouseCallback auxiliaryMouseCallback;
static FilesScrollWheelCallback scrollWheelCallback;
static void *auxiliaryMouseCallbackContext;
static atomic_bool mainMenuInstalled;
static atomic_bool gridScrollCaptureEnabled;
static atomic_int mainMenuRootCount;
static atomic_int mainMenuCommandCount;
static NSPasteboardType const FilesCutPasteboardType = @"io.filescommunity.files.cut-items";
static NSString *const FilesMetadataItemFSInvisibleKey = @"kMDItemFSInvisible";
static NSString *const FilesMetadataItemTextContentKey = @"kMDItemTextContent";
static NSString *const FilesMetadataItemContentTypeTreeKey = @"kMDItemContentTypeTree";
static NSString *const FilesMetadataItemFSSizeKey = @"kMDItemFSSize";
static NSString *const FilesMetadataItemFSContentChangeDateKey = @"kMDItemFSContentChangeDate";
static NSString *const FilesMetadataItemUserTagsKey = @"kMDItemUserTags";

__attribute__((visibility("default"))) void files_macos_install_auxiliary_mouse_handler(
	FilesAuxiliaryMouseCallback callback,
	FilesScrollWheelCallback scrollCallback,
	void *callbackContext)
{
	dispatch_async(dispatch_get_main_queue(), ^{
		if (auxiliaryMouseMonitor != nil)
		{
			[NSEvent removeMonitor:auxiliaryMouseMonitor];
			auxiliaryMouseMonitor = nil;
		}
		auxiliaryMouseCallback = callback;
		scrollWheelCallback = scrollCallback;
		auxiliaryMouseCallbackContext = callbackContext;
		if (callback == NULL && scrollCallback == NULL)
		{
			return;
		}

		NSEventMask eventMask = NSEventMaskOtherMouseDown | NSEventMaskScrollWheel;
		auxiliaryMouseMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:eventMask handler:^NSEvent *(NSEvent *event) {
			if (event.type == NSEventTypeOtherMouseDown)
			{
				NSInteger buttonNumber = event.buttonNumber;
				if (buttonNumber >= 2 && buttonNumber <= 4 && auxiliaryMouseCallback != NULL)
				{
					auxiliaryMouseCallback(auxiliaryMouseCallbackContext, (int)buttonNumber);
					return nil;
				}
			}
			else if (event.type == NSEventTypeScrollWheel && atomic_load(&gridScrollCaptureEnabled) && scrollWheelCallback != NULL &&
				scrollWheelCallback(
					auxiliaryMouseCallbackContext,
					event.scrollingDeltaX,
					event.scrollingDeltaY,
					event.hasPreciseScrollingDeltas ? 1 : 0) != 0)
			{
				return nil;
			}
			return event;
		}];
	});
}

__attribute__((visibility("default"))) void files_macos_uninstall_auxiliary_mouse_handler(void)
{
	dispatch_async(dispatch_get_main_queue(), ^{
		if (auxiliaryMouseMonitor != nil)
		{
			[NSEvent removeMonitor:auxiliaryMouseMonitor];
			auxiliaryMouseMonitor = nil;
		}
		auxiliaryMouseCallback = NULL;
		scrollWheelCallback = NULL;
		auxiliaryMouseCallbackContext = NULL;
	});
}

__attribute__((visibility("default"))) void files_macos_set_grid_scroll_capture(int isEnabled)
{
	atomic_store(&gridScrollCaptureEnabled, isEnabled != 0);
}

typedef struct
{
	atomic_bool cancelled;
} FilesSpotlightSearchContext;

typedef int (*FilesCoordinatedOperation)(void *context, const char *sourcePath, const char *destinationPath);
typedef void (*FilesDirectoryChangedCallback)(void *context);

typedef struct
{
	FSEventStreamRef stream;
	void *queue;
	char *rootPath;
	bool includeSubdirectories;
	FilesDirectoryChangedCallback callback;
	void *callbackContext;
} FilesDirectoryChangeContext;

typedef struct
{
	void *url;
} FilesSecurityScopedAccessContext;

static void files_directory_changed(
	ConstFSEventStreamRef stream,
	void *clientCallbackInfo,
	size_t eventCount,
	void *eventPaths,
	const FSEventStreamEventFlags eventFlags[],
	const FSEventStreamEventId eventIds[])
{
	FilesDirectoryChangeContext *context = clientCallbackInfo;
	if (context == NULL || context->callback == NULL)
	{
		return;
	}

	BOOL shouldNotify = context->includeSubdirectories;
	if (!shouldNotify)
	{
		char **paths = eventPaths;
		NSString *rootPath = [NSString stringWithUTF8String:context->rootPath];
		for (size_t index = 0; index < eventCount; index++)
		{
			NSString *eventPath = [NSString stringWithUTF8String:paths[index]];
			if ([eventPath isEqualToString:rootPath] ||
				[[eventPath stringByDeletingLastPathComponent] isEqualToString:rootPath])
			{
				shouldNotify = YES;
				break;
			}
		}
	}

	if (shouldNotify)
	{
		context->callback(context->callbackContext);
	}
}

static NSURL *files_url_from_path(const char *path)
{
	if (path == NULL)
	{
		return nil;
	}

	NSString *value = [NSString stringWithUTF8String:path];
	return value == nil ? nil : [NSURL fileURLWithPath:value];
}

static char *files_copy_error(NSError *error)
{
	if (error == nil)
	{
		return NULL;
	}

	const char *message = error.localizedDescription.UTF8String;
	return message == NULL ? NULL : strdup(message);
}

static char *files_copy_utf8_string(NSString *value)
{
	const char *text = value.UTF8String;
	return text == NULL ? NULL : strdup(text);
}

static char *files_copy_json(id value)
{
	NSError *jsonError = nil;
	NSData *jsonData = [NSJSONSerialization dataWithJSONObject:value options:0 error:&jsonError];
	if (jsonData == nil)
	{
		return NULL;
	}

	NSString *json = [[NSString alloc] initWithData:jsonData encoding:NSUTF8StringEncoding];
	return files_copy_utf8_string(json);
}

static NSMutableDictionary<NSString *, NSDictionary *> *windowPlacementCache;

static void files_update_window_placement_cache(NSWindow *window)
{
	NSString *identifier = window.identifier;
	if (identifier.length == 0)
	{
		return;
	}

	NSRect frame = window.frame;
	NSDictionary *placement = @{
		@"X": @(frame.origin.x),
		@"Y": @(frame.origin.y),
		@"Width": @(frame.size.width),
		@"Height": @(frame.size.height),
		@"IsMaximized": @(window.isZoomed),
		@"UsesUnifiedTitleBar": ((window.styleMask & NSWindowStyleMaskFullSizeContentView) != 0 && window.titleVisibility == NSWindowTitleHidden) ? @YES : @NO,
	};
	@synchronized([NSWindow class])
	{
		windowPlacementCache = windowPlacementCache ?: [NSMutableDictionary dictionary];
		windowPlacementCache[identifier] = placement;
	}
}

@interface FilesWindowObserver : NSObject
- (void)windowFrameDidChange:(NSNotification *)notification;
- (void)windowWillClose:(NSNotification *)notification;
@end

@implementation FilesWindowObserver

- (void)windowFrameDidChange:(NSNotification *)notification
{
	files_update_window_placement_cache(notification.object);
}

- (void)windowWillClose:(NSNotification *)notification
{
	NSWindow *window = notification.object;
	NSString *identifier = window.identifier;
	if (identifier.length == 0)
	{
		return;
	}
	files_update_window_placement_cache(window);
}

@end

static FilesWindowObserver *windowObserver;

static void files_run_on_main_async(dispatch_block_t block)
{
	if ([NSThread isMainThread])
	{
		block();
	}
	else
	{
		dispatch_async(dispatch_get_main_queue(), block);
	}
}

static NSWindow *files_window_with_identifier(NSString *identifier)
{
	for (NSWindow *window in NSApp.windows)
	{
		if ([window.identifier isEqualToString:identifier])
		{
			return window;
		}
	}
	return nil;
}

__attribute__((visibility("default"))) int files_macos_get_accessibility_display_options(void)
{
	@autoreleasepool
	{
		NSWorkspace *workspace = NSWorkspace.sharedWorkspace;
		int options = 0;
		if (workspace.accessibilityDisplayShouldIncreaseContrast)
		{
			options |= 1;
		}
		if (workspace.accessibilityDisplayShouldReduceTransparency)
		{
			options |= 2;
		}
		if (workspace.accessibilityDisplayShouldReduceMotion)
		{
			options |= 4;
		}
		return options;
	}
}

__attribute__((visibility("default"))) unsigned int files_macos_get_accent_color_argb(void)
{
	@autoreleasepool
	{
		NSColor *color = [NSColor.controlAccentColor colorUsingColorSpace:NSColorSpace.sRGBColorSpace];
		if (color == nil)
		{
			return 0;
		}

		unsigned int red = (unsigned int)lround(fmin(1, fmax(0, color.redComponent)) * 255);
		unsigned int green = (unsigned int)lround(fmin(1, fmax(0, color.greenComponent)) * 255);
		unsigned int blue = (unsigned int)lround(fmin(1, fmax(0, color.blueComponent)) * 255);
		return 0xFF000000u | (red << 16) | (green << 8) | blue;
	}
}

__attribute__((visibility("default"))) int files_macos_announce_accessibility(const char *announcement, int highPriority)
{
	@autoreleasepool
	{
		if (announcement == NULL)
		{
			return 0;
		}
		NSString *text = [NSString stringWithUTF8String:announcement];
		id element = NSApp.keyWindow ?: NSApp.mainWindow;
		if (text.length == 0 || element == nil)
		{
			return 0;
		}

		NSAccessibilityPriorityLevel priority = highPriority != 0
			? NSAccessibilityPriorityHigh
			: NSAccessibilityPriorityMedium;
		NSAccessibilityPostNotificationWithUserInfo(
			element,
			NSAccessibilityAnnouncementRequestedNotification,
			@{
				NSAccessibilityAnnouncementKey: text,
				NSAccessibilityPriorityKey: @(priority),
			});
		return 1;
	}
}

__attribute__((visibility("default"))) int files_macos_register_window(void *windowHandle, const char *identifier)
{
	if (windowHandle == NULL || identifier == NULL)
	{
		return 0;
	}

	NSString *value = [NSString stringWithUTF8String:identifier];
	if (value.length == 0)
	{
		return 0;
	}

	NSWindow *targetWindow = (__bridge NSWindow *)windowHandle;
	files_run_on_main_async(^{
		NSWindow *window = targetWindow;
		if ([window isKindOfClass:NSWindow.class])
		{
			NSWorkspace *workspace = NSWorkspace.sharedWorkspace;
			window.identifier = value;
			window.titleVisibility = NSWindowTitleHidden;
			window.titlebarAppearsTransparent = !workspace.accessibilityDisplayShouldReduceTransparency;
			window.titlebarSeparatorStyle = workspace.accessibilityDisplayShouldIncreaseContrast
				? NSTitlebarSeparatorStyleAutomatic
				: NSTitlebarSeparatorStyleNone;
			window.animationBehavior = workspace.accessibilityDisplayShouldReduceMotion
				? NSWindowAnimationBehaviorNone
				: NSWindowAnimationBehaviorDefault;
			window.styleMask |= NSWindowStyleMaskFullSizeContentView;
			window.tabbingMode = NSWindowTabbingModeDisallowed;
			if (windowObserver == nil)
			{
				windowObserver = [FilesWindowObserver new];
				NSNotificationCenter *center = NSNotificationCenter.defaultCenter;
				[center addObserver:windowObserver selector:@selector(windowFrameDidChange:) name:NSWindowDidMoveNotification object:nil];
				[center addObserver:windowObserver selector:@selector(windowFrameDidChange:) name:NSWindowDidResizeNotification object:nil];
				[center addObserver:windowObserver selector:@selector(windowFrameDidChange:) name:NSWindowDidEndLiveResizeNotification object:nil];
				[center addObserver:windowObserver selector:@selector(windowFrameDidChange:) name:NSWindowDidExitFullScreenNotification object:nil];
				[center addObserver:windowObserver selector:@selector(windowFrameDidChange:) name:NSWindowDidEnterFullScreenNotification object:nil];
				[center addObserver:windowObserver selector:@selector(windowWillClose:) name:NSWindowWillCloseNotification object:nil];
			}
			files_update_window_placement_cache(window);
		}
	});
	return 1;
}

__attribute__((visibility("default"))) char *files_macos_get_window_placement(const char *identifier)
{
	if (identifier == NULL)
	{
		return NULL;
	}

	NSString *value = [NSString stringWithUTF8String:identifier];
	NSDictionary *placement = nil;
	@synchronized([NSWindow class])
	{
		placement = windowPlacementCache[value];
	}
	return placement == nil ? NULL : files_copy_json(placement);
}

__attribute__((visibility("default"))) int files_macos_set_window_placement(
	const char *identifier,
	double x,
	double y,
	double width,
	double height,
	int isMaximized)
{
	if (identifier == NULL || !isfinite(x) || !isfinite(y) || !isfinite(width) || !isfinite(height))
	{
		return 0;
	}

	NSString *value = [NSString stringWithUTF8String:identifier];
	files_run_on_main_async(^{
		NSWindow *window = files_window_with_identifier(value);
		if (window == nil)
		{
			return;
		}

		NSRect requestedFrame = NSMakeRect(x, y, MAX(width, 640), MAX(height, 480));
		NSScreen *targetScreen = nil;
		double largestIntersection = 0;
		for (NSScreen *screen in NSScreen.screens)
		{
			NSRect intersection = NSIntersectionRect(requestedFrame, screen.visibleFrame);
			double area = intersection.size.width * intersection.size.height;
			if (area > largestIntersection)
			{
				largestIntersection = area;
				targetScreen = screen;
			}
		}
		targetScreen = targetScreen ?: NSScreen.mainScreen;
		if (targetScreen == nil)
		{
			return;
		}

		NSRect visibleFrame = targetScreen.visibleFrame;
		requestedFrame.size.width = MIN(requestedFrame.size.width, visibleFrame.size.width);
		requestedFrame.size.height = MIN(requestedFrame.size.height, visibleFrame.size.height);
		requestedFrame.origin.x = MIN(MAX(requestedFrame.origin.x, NSMinX(visibleFrame)), NSMaxX(visibleFrame) - requestedFrame.size.width);
		requestedFrame.origin.y = MIN(MAX(requestedFrame.origin.y, NSMinY(visibleFrame)), NSMaxY(visibleFrame) - requestedFrame.size.height);
		[window setFrame:requestedFrame display:YES];
		if (isMaximized != 0 && !window.isZoomed)
		{
			[window zoom:nil];
		}
		files_update_window_placement_cache(window);
	});
	return 1;
}

static NSMenuItem *files_add_menu_command(NSMenu *menu, NSString *title, NSString *key, NSEventModifierFlags modifiers, NSInteger command)
{
	NSMenuItem *item = [[NSMenuItem alloc] initWithTitle:title action:@selector(executeCommand:) keyEquivalent:key ?: @""];
	item.target = mainMenuTarget;
	item.tag = command;
	item.keyEquivalentModifierMask = modifiers;
	[menu addItem:item];
	return item;
}

static NSMenuItem *files_add_standard_menu_item(NSMenu *menu, NSString *title, SEL action, NSString *key, NSEventModifierFlags modifiers)
{
	NSMenuItem *item = [[NSMenuItem alloc] initWithTitle:title action:action keyEquivalent:key ?: @""];
	item.keyEquivalentModifierMask = modifiers;
	[menu addItem:item];
	return item;
}

static NSMenu *files_add_main_submenu(NSMenu *mainMenu, NSString *title)
{
	NSMenu *submenu = [[NSMenu alloc] initWithTitle:title];
	NSMenuItem *root = [[NSMenuItem alloc] initWithTitle:title action:nil keyEquivalent:@""];
	[mainMenu addItem:root];
	[mainMenu setSubmenu:submenu forItem:root];
	return submenu;
}

static NSInteger files_count_menu_commands(NSMenu *menu);

__attribute__((visibility("default"))) void files_macos_install_main_menu(
	const char *language,
	FilesMenuCommandCallback executeCallback,
	FilesMenuValidationCallback validateCallback,
	void *callbackContext)
{
	void (^installBlock)(void) = ^{
		@autoreleasepool
		{
			BOOL zh = language != NULL && strcmp(language, "zh-Hans") == 0;
			mainMenuTarget = [FilesMenuTarget new];
			mainMenuTarget.executeCallback = executeCallback;
			mainMenuTarget.validateCallback = validateCallback;
			mainMenuTarget.callbackContext = callbackContext;

			NSMenu *mainMenu = [[NSMenu alloc] initWithTitle:@""];
			NSMenu *appMenu = files_add_main_submenu(mainMenu, @"Files");
			files_add_standard_menu_item(appMenu, zh ? @"关于 Files" : @"About Files", @selector(orderFrontStandardAboutPanel:), @"", 0);
			[appMenu addItem:[NSMenuItem separatorItem]];
			files_add_menu_command(appMenu, zh ? @"设置…" : @"Settings…", @",", NSEventModifierFlagCommand, 1);
			[appMenu addItem:[NSMenuItem separatorItem]];
			files_add_standard_menu_item(appMenu, zh ? @"隐藏 Files" : @"Hide Files", @selector(hide:), @"h", NSEventModifierFlagCommand);
			files_add_standard_menu_item(appMenu, zh ? @"隐藏其他" : @"Hide Others", @selector(hideOtherApplications:), @"h", NSEventModifierFlagCommand | NSEventModifierFlagOption);
			files_add_standard_menu_item(appMenu, zh ? @"全部显示" : @"Show All", @selector(unhideAllApplications:), @"", 0);
			[appMenu addItem:[NSMenuItem separatorItem]];
			files_add_standard_menu_item(appMenu, zh ? @"退出 Files" : @"Quit Files", @selector(terminate:), @"q", NSEventModifierFlagCommand);

			NSMenu *fileMenu = files_add_main_submenu(mainMenu, zh ? @"文件" : @"File");
			files_add_menu_command(fileMenu, zh ? @"新建窗口" : @"New Window", @"n", NSEventModifierFlagCommand, 33);
			files_add_menu_command(fileMenu, zh ? @"新建标签页" : @"New Tab", @"t", NSEventModifierFlagCommand, 2);
			files_add_menu_command(fileMenu, zh ? @"复制标签页" : @"Duplicate Tab", @"k", NSEventModifierFlagCommand | NSEventModifierFlagShift, 37);
			files_add_menu_command(fileMenu, zh ? @"将标签页移到新窗口" : @"Move Tab to New Window", @"", 0, 40);
			files_add_menu_command(fileMenu, zh ? @"在新标签页中打开" : @"Open in New Tab", @"\r", NSEventModifierFlagCommand, 30);
			files_add_menu_command(fileMenu, zh ? @"新建文件夹" : @"New Folder", @"n", NSEventModifierFlagCommand | NSEventModifierFlagShift, 3);
			files_add_menu_command(fileMenu, zh ? @"打开文件夹…" : @"Open Folder…", @"o", NSEventModifierFlagCommand, 25);
			files_add_menu_command(fileMenu, zh ? @"关闭标签页" : @"Close Tab", @"w", NSEventModifierFlagCommand, 4);
			files_add_menu_command(fileMenu, zh ? @"关闭左侧标签页" : @"Close Tabs to the Left", @"", 0, 38);
			files_add_menu_command(fileMenu, zh ? @"关闭右侧标签页" : @"Close Tabs to the Right", @"", 0, 39);
			files_add_menu_command(fileMenu, zh ? @"重新打开关闭的标签页" : @"Reopen Closed Tab", @"t", NSEventModifierFlagCommand | NSEventModifierFlagShift, 35);
			files_add_menu_command(fileMenu, zh ? @"关闭其他标签页" : @"Close Other Tabs", @"", 0, 36);
			[fileMenu addItem:[NSMenuItem separatorItem]];
			files_add_menu_command(fileMenu, zh ? @"显示简介" : @"Get Info", @"i", NSEventModifierFlagCommand, 5);
			files_add_menu_command(fileMenu, zh ? @"打开方式…" : @"Open With…", @"", 0, 29);
			files_add_menu_command(fileMenu, zh ? @"重命名" : @"Rename", @"", 0, 7);
			files_add_menu_command(fileMenu, zh ? @"制作副本" : @"Duplicate", @"d", NSEventModifierFlagCommand, 31);
			files_add_menu_command(fileMenu, zh ? @"制作符号链接" : @"Make Symbolic Link", @"l", NSEventModifierFlagCommand | NSEventModifierFlagOption, 32);
			files_add_menu_command(fileMenu, zh ? @"移到废纸篓" : @"Move to Trash", @"\x7F", NSEventModifierFlagCommand, 6);
			files_add_menu_command(fileMenu, zh ? @"立即删除…" : @"Delete Immediately…", @"\x7F", NSEventModifierFlagCommand | NSEventModifierFlagOption, 28);

			NSMenu *editMenu = files_add_main_submenu(mainMenu, zh ? @"编辑" : @"Edit");
			files_add_menu_command(editMenu, zh ? @"撤销" : @"Undo", @"z", NSEventModifierFlagCommand, 8);
			files_add_menu_command(editMenu, zh ? @"重做" : @"Redo", @"z", NSEventModifierFlagCommand | NSEventModifierFlagShift, 9);
			[editMenu addItem:[NSMenuItem separatorItem]];
			files_add_menu_command(editMenu, zh ? @"剪切" : @"Cut", @"x", NSEventModifierFlagCommand, 10);
			files_add_menu_command(editMenu, zh ? @"复制" : @"Copy", @"c", NSEventModifierFlagCommand, 11);
			files_add_menu_command(editMenu, zh ? @"粘贴" : @"Paste", @"v", NSEventModifierFlagCommand, 12);
			files_add_menu_command(editMenu, zh ? @"全选" : @"Select All", @"a", NSEventModifierFlagCommand, 13);
			files_add_menu_command(editMenu, zh ? @"复制路径" : @"Copy Path", @"c", NSEventModifierFlagCommand | NSEventModifierFlagShift, 14);

			NSMenu *viewMenu = files_add_main_submenu(mainMenu, zh ? @"显示" : @"View");
			files_add_menu_command(viewMenu, zh ? @"搜索" : @"Search", @"f", NSEventModifierFlagCommand, 15);
			files_add_menu_command(viewMenu, zh ? @"编辑地址" : @"Edit Address", @"l", NSEventModifierFlagCommand, 16);
			[viewMenu addItem:[NSMenuItem separatorItem]];
			files_add_menu_command(viewMenu, zh ? @"网格" : @"Grid", @"1", NSEventModifierFlagCommand, 17);
			files_add_menu_command(viewMenu, zh ? @"详细信息" : @"Details", @"2", NSEventModifierFlagCommand, 18);
			files_add_menu_command(viewMenu, zh ? @"显示预览" : @"Show Preview", @"p", NSEventModifierFlagCommand | NSEventModifierFlagShift, 19);
			files_add_menu_command(viewMenu, zh ? @"显示侧栏" : @"Show Sidebar", @"s", NSEventModifierFlagCommand | NSEventModifierFlagOption, 20);

			NSMenu *goMenu = files_add_main_submenu(mainMenu, zh ? @"前往" : @"Go");
			files_add_menu_command(goMenu, zh ? @"后退" : @"Back", @"[", NSEventModifierFlagCommand, 21);
			files_add_menu_command(goMenu, zh ? @"前进" : @"Forward", @"]", NSEventModifierFlagCommand, 22);
			unichar upArrow = NSUpArrowFunctionKey;
			files_add_menu_command(goMenu, zh ? @"上一级" : @"Enclosing Folder", [NSString stringWithCharacters:&upArrow length:1], NSEventModifierFlagCommand, 23);
			files_add_menu_command(goMenu, zh ? @"个人目录" : @"Home", @"h", NSEventModifierFlagCommand | NSEventModifierFlagShift, 24);
			[goMenu addItem:[NSMenuItem separatorItem]];
			files_add_menu_command(goMenu, zh ? @"连接服务器…" : @"Connect to Server…", @"k", NSEventModifierFlagCommand, 26);
			files_add_menu_command(goMenu, zh ? @"在终端中打开" : @"Open in Terminal", @"", 0, 27);

			NSMenu *windowMenu = files_add_main_submenu(mainMenu, zh ? @"窗口" : @"Window");
			files_add_standard_menu_item(windowMenu, zh ? @"最小化" : @"Minimize", @selector(performMiniaturize:), @"m", NSEventModifierFlagCommand);
			files_add_standard_menu_item(windowMenu, zh ? @"缩放" : @"Zoom", @selector(performZoom:), @"", 0);
			files_add_menu_command(windowMenu, zh ? @"显示下一个标签页" : @"Show Next Tab", @"\t", NSEventModifierFlagControl, 41);
			files_add_menu_command(windowMenu, zh ? @"显示上一个标签页" : @"Show Previous Tab", @"\t", NSEventModifierFlagControl | NSEventModifierFlagShift, 42);
			[windowMenu addItem:[NSMenuItem separatorItem]];
			files_add_menu_command(windowMenu, zh ? @"关闭窗口" : @"Close Window", @"w", NSEventModifierFlagCommand | NSEventModifierFlagShift, 34);
			[NSApp setWindowsMenu:windowMenu];
			[NSApp setMainMenu:mainMenu];
			atomic_store(&mainMenuRootCount, (int)mainMenu.numberOfItems);
			atomic_store(&mainMenuCommandCount, (int)files_count_menu_commands(mainMenu));
			atomic_store(&mainMenuInstalled, true);
		}
	};

	if ([NSThread isMainThread])
	{
		installBlock();
	}
	else
	{
		dispatch_async(dispatch_get_main_queue(), installBlock);
	}
}

__attribute__((visibility("default"))) void files_macos_uninstall_main_menu(void)
{
	mainMenuTarget.executeCallback = NULL;
	mainMenuTarget.validateCallback = NULL;
	mainMenuTarget.callbackContext = NULL;
	mainMenuTarget = nil;
	atomic_store(&mainMenuInstalled, false);
	atomic_store(&mainMenuRootCount, 0);
	atomic_store(&mainMenuCommandCount, 0);
}

static NSInteger files_count_menu_commands(NSMenu *menu)
{
	NSInteger count = 0;
	for (NSMenuItem *item in menu.itemArray)
	{
		if (item.target == mainMenuTarget && item.action == @selector(executeCommand:))
		{
			count++;
		}
		if (item.submenu != nil)
		{
			count += files_count_menu_commands(item.submenu);
		}
	}
	return count;
}

__attribute__((visibility("default"))) char *files_macos_describe_main_menu(void)
{
	return files_copy_json(@{
		@"installed": @(atomic_load(&mainMenuInstalled)),
		@"rootCount": @(atomic_load(&mainMenuRootCount)),
		@"commandCount": @(atomic_load(&mainMenuCommandCount))
	});
}

__attribute__((visibility("default"))) int files_macos_invoke_main_menu_command(int command)
{
	FilesMenuTarget *target = mainMenuTarget;
	if (!atomic_load(&mainMenuInstalled) || target == nil || target.executeCallback == NULL)
	{
		return 0;
	}
	NSMenuItem *item = [[NSMenuItem alloc] initWithTitle:@"" action:nil keyEquivalent:@""];
	item.tag = command;
	[target executeCommand:item];
	return 1;
}

static NSDictionary *files_security_bookmark_result(NSURL *url)
{
	NSError *bookmarkError = nil;
	NSData *bookmark = [url bookmarkDataWithOptions:NSURLBookmarkCreationWithSecurityScope
		includingResourceValuesForKeys:nil
		relativeToURL:nil
		error:&bookmarkError];
	if (bookmark == nil)
	{
		return @{
			@"success": @NO,
			@"canceled": @NO,
			@"path": url.path ?: @"",
			@"bookmark": @"",
			@"error": bookmarkError.localizedDescription ?: @"The folder access bookmark couldn't be created."
		};
	}

	return @{
		@"success": @YES,
		@"canceled": @NO,
		@"path": url.path ?: @"",
		@"bookmark": [bookmark base64EncodedStringWithOptions:0],
		@"error": @""
	};
}

__attribute__((visibility("default"))) char *files_macos_create_security_bookmark(const char *path)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		return files_copy_json(url == nil
			? @{
				@"success": @NO,
				@"canceled": @NO,
				@"path": @"",
				@"bookmark": @"",
				@"error": @"Invalid folder URL."
			}
			: files_security_bookmark_result(url));
	}
}

__attribute__((visibility("default"))) char *files_macos_pick_folder(const char *initialPath)
{
	__block char *result = NULL;
	void (^pickerBlock)(void) = ^{
		@autoreleasepool
		{
			NSOpenPanel *panel = [NSOpenPanel openPanel];
			panel.canChooseFiles = NO;
			panel.canChooseDirectories = YES;
			panel.allowsMultipleSelection = NO;
			panel.canCreateDirectories = YES;
			NSURL *initialURL = files_url_from_path(initialPath);
			if (initialURL != nil)
			{
				panel.directoryURL = initialURL;
			}

			if ([panel runModal] != NSModalResponseOK || panel.URL == nil)
			{
				result = files_copy_json(@{
					@"success": @NO,
					@"canceled": @YES,
					@"path": @"",
					@"bookmark": @"",
					@"error": @""
				});
				return;
			}

			result = files_copy_json(files_security_bookmark_result(panel.URL));
		}
	};

	if ([NSThread isMainThread])
	{
		pickerBlock();
	}
	else
	{
		dispatch_sync(dispatch_get_main_queue(), pickerBlock);
	}
	return result;
}

__attribute__((visibility("default"))) void *files_macos_access_security_bookmark(
	const char *bookmarkValue,
	char **resultJson)
{
	@autoreleasepool
	{
		if (resultJson == NULL)
		{
			return NULL;
		}
		*resultJson = NULL;
		NSString *encodedBookmark = bookmarkValue == NULL ? nil : [NSString stringWithUTF8String:bookmarkValue];
		NSData *bookmark = encodedBookmark == nil
			? nil
			: [[NSData alloc] initWithBase64EncodedString:encodedBookmark options:0];
		if (bookmark == nil)
		{
			*resultJson = files_copy_json(@{ @"success": @NO, @"error": @"The folder access bookmark is invalid." });
			return NULL;
		}

		NSError *resolutionError = nil;
		BOOL isStale = NO;
		NSURL *url = [NSURL URLByResolvingBookmarkData:bookmark
			options:NSURLBookmarkResolutionWithSecurityScope | NSURLBookmarkResolutionWithoutUI
			relativeToURL:nil
			bookmarkDataIsStale:&isStale
			error:&resolutionError];
		if (url == nil)
		{
			*resultJson = files_copy_json(@{
				@"success": @NO,
				@"error": resolutionError.localizedDescription ?: @"The folder access bookmark couldn't be resolved."
			});
			return NULL;
		}

		NSData *currentBookmark = bookmark;
		if (isStale)
		{
			NSError *refreshError = nil;
			currentBookmark = [url bookmarkDataWithOptions:NSURLBookmarkCreationWithSecurityScope
				includingResourceValuesForKeys:nil
				relativeToURL:nil
				error:&refreshError];
			if (currentBookmark == nil)
			{
				*resultJson = files_copy_json(@{
					@"success": @NO,
					@"error": refreshError.localizedDescription ?: @"The folder access bookmark couldn't be refreshed."
				});
				return NULL;
			}
		}

		if (![url startAccessingSecurityScopedResource])
		{
			*resultJson = files_copy_json(@{ @"success": @NO, @"error": @"macOS denied access to the selected folder." });
			return NULL;
		}

		FilesSecurityScopedAccessContext *context = calloc(1, sizeof(FilesSecurityScopedAccessContext));
		if (context == NULL)
		{
			[url stopAccessingSecurityScopedResource];
			*resultJson = files_copy_json(@{ @"success": @NO, @"error": @"The folder access context couldn't be allocated." });
			return NULL;
		}

		context->url = (__bridge_retained void *)url;
		*resultJson = files_copy_json(@{
			@"success": @YES,
			@"path": url.path ?: @"",
			@"bookmark": [currentBookmark base64EncodedStringWithOptions:0],
			@"stale": @(isStale),
			@"error": @""
		});
		return context;
	}
}

__attribute__((visibility("default"))) void files_macos_stop_security_bookmark_access(void *accessContext)
{
	FilesSecurityScopedAccessContext *context = accessContext;
	if (context == NULL)
	{
		return;
	}

	NSURL *url = (__bridge_transfer NSURL *)context->url;
	[url stopAccessingSecurityScopedResource];
	free(context);
}

__attribute__((visibility("default"))) void *files_macos_fsevents_create(
	const char *path,
	int includeSubdirectories,
	FilesDirectoryChangedCallback callback,
	void *callbackContext)
{
	@autoreleasepool
	{
		if (path == NULL || callback == NULL)
		{
			return NULL;
		}

		NSString *inputPath = [[NSString stringWithUTF8String:path] stringByStandardizingPath];
		char *resolvedPath = realpath(inputPath.fileSystemRepresentation, NULL);
		NSString *rootPath = resolvedPath == NULL
			? inputPath
			: [[NSFileManager defaultManager] stringWithFileSystemRepresentation:resolvedPath length:strlen(resolvedPath)];
		free(resolvedPath);
		if (rootPath == nil)
		{
			return NULL;
		}

		FilesDirectoryChangeContext *context = calloc(1, sizeof(FilesDirectoryChangeContext));
		if (context == NULL)
		{
			return NULL;
		}

		context->rootPath = strdup(rootPath.UTF8String);
		context->includeSubdirectories = includeSubdirectories != 0;
		context->callback = callback;
		context->callbackContext = callbackContext;
		FSEventStreamContext streamContext = { 0, context, NULL, NULL, NULL };
		FSEventStreamEventId startingEventId = FSEventsGetCurrentEventId();
		context->stream = FSEventStreamCreate(
			NULL,
			&files_directory_changed,
			&streamContext,
			(__bridge CFArrayRef)@[ rootPath ],
			startingEventId,
			0.1,
			kFSEventStreamCreateFlagFileEvents |
				kFSEventStreamCreateFlagWatchRoot |
				kFSEventStreamCreateFlagNoDefer);
		if (context->stream == NULL)
		{
			free(context->rootPath);
			free(context);
			return NULL;
		}

		dispatch_queue_t queue = dispatch_queue_create("io.filescommunity.files.macos.fsevents", DISPATCH_QUEUE_SERIAL);
		context->queue = (__bridge_retained void *)queue;
		FSEventStreamSetDispatchQueue(context->stream, queue);
		if (!FSEventStreamStart(context->stream))
		{
			FSEventStreamInvalidate(context->stream);
			FSEventStreamRelease(context->stream);
			dispatch_queue_t ownedQueue = (__bridge_transfer dispatch_queue_t)context->queue;
			(void)ownedQueue;
			free(context->rootPath);
			free(context);
			return NULL;
		}

		return context;
	}
}

__attribute__((visibility("default"))) void files_macos_fsevents_destroy(void *monitor)
{
	FilesDirectoryChangeContext *context = monitor;
	if (context == NULL)
	{
		return;
	}

	dispatch_queue_t queue = (__bridge dispatch_queue_t)context->queue;
	FSEventStreamStop(context->stream);
	FSEventStreamInvalidate(context->stream);
	FSEventStreamRelease(context->stream);
	dispatch_sync(queue, ^{});
	dispatch_queue_t ownedQueue = (__bridge_transfer dispatch_queue_t)context->queue;
	(void)ownedQueue;
	free(context->rootPath);
	free(context);
}

__attribute__((visibility("default"))) int files_macos_open_path(const char *path)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		return url != nil && [[NSWorkspace sharedWorkspace] openURL:url] ? 1 : 0;
	}
}

__attribute__((visibility("default"))) int files_macos_is_file_package(const char *path)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		if (url == nil)
		{
			return -1;
		}

		NSNumber *isPackage = nil;
		NSError *error = nil;
		if (![url getResourceValue:&isPackage forKey:NSURLIsPackageKey error:&error] || isPackage == nil)
		{
			return -1;
		}

		return isPackage.boolValue ? 1 : 0;
	}
}

__attribute__((visibility("default"))) char *files_macos_get_open_with_applications(const char *path)
{
	@autoreleasepool
	{
		NSURL *fileURL = files_url_from_path(path);
		if (fileURL == nil)
		{
			return files_copy_json(@[]);
		}

		NSWorkspace *workspace = [NSWorkspace sharedWorkspace];
		NSURL *defaultURL = [workspace URLForApplicationToOpenURL:fileURL];
		NSArray<NSURL *> *applicationURLs = [workspace URLsForApplicationsToOpenURL:fileURL];
		NSMutableArray *applications = [NSMutableArray arrayWithCapacity:applicationURLs.count];
		NSMutableSet<NSString *> *knownPaths = [NSMutableSet set];
		for (NSURL *applicationURL in applicationURLs)
		{
			NSString *applicationPath = applicationURL.path;
			if (applicationPath.length == 0 || [knownPaths containsObject:applicationPath])
			{
				continue;
			}
			[knownPaths addObject:applicationPath];

			NSString *localizedName = nil;
			[applicationURL getResourceValue:&localizedName forKey:NSURLLocalizedNameKey error:nil];
			if (localizedName.length == 0)
			{
				localizedName = applicationURL.lastPathComponent.stringByDeletingPathExtension;
			}
			NSBundle *bundle = [NSBundle bundleWithURL:applicationURL];
			BOOL isDefault = defaultURL != nil && [defaultURL.path isEqualToString:applicationPath];
			[applications addObject:@{
				@"name": localizedName ?: applicationPath,
				@"applicationPath": applicationPath,
				@"bundleIdentifier": bundle.bundleIdentifier ?: @"",
				@"isDefault": @(isDefault)
			}];
		}

		[applications sortUsingComparator:^NSComparisonResult(NSDictionary *left, NSDictionary *right) {
			BOOL leftDefault = [left[@"isDefault"] boolValue];
			BOOL rightDefault = [right[@"isDefault"] boolValue];
			if (leftDefault != rightDefault)
			{
				return leftDefault ? NSOrderedAscending : NSOrderedDescending;
			}
			return [left[@"name"] localizedStandardCompare:right[@"name"]];
		}];
		return files_copy_json(applications);
	}
}

__attribute__((visibility("default"))) char *files_macos_open_path_with_application(
	const char *path,
	const char *applicationPath)
{
	@autoreleasepool
	{
		NSURL *fileURL = files_url_from_path(path);
		NSURL *applicationURL = files_url_from_path(applicationPath);
		if (fileURL == nil || applicationURL == nil ||
			![[NSFileManager defaultManager] fileExistsAtPath:fileURL.path] ||
			![[NSFileManager defaultManager] fileExistsAtPath:applicationURL.path])
		{
			return strdup("The file or application is no longer available.");
		}

		dispatch_semaphore_t completion = dispatch_semaphore_create(0);
		__block NSError *openError = nil;
		dispatch_async(dispatch_get_main_queue(), ^{
			NSWorkspaceOpenConfiguration *configuration = [NSWorkspaceOpenConfiguration configuration];
			[[NSWorkspace sharedWorkspace] openURLs:@[ fileURL ]
				withApplicationAtURL:applicationURL
				configuration:configuration
				completionHandler:^(NSRunningApplication *application, NSError *error) {
					openError = error;
					dispatch_semaphore_signal(completion);
				}];
		});
		if (dispatch_semaphore_wait(completion, dispatch_time(DISPATCH_TIME_NOW, 30 * NSEC_PER_SEC)) != 0)
		{
			return strdup("The application didn't respond while opening the file.");
		}
		return files_copy_error(openError);
	}
}

__attribute__((visibility("default"))) char *files_macos_pick_application(void)
{
	__block char *result = NULL;
	void (^pickerBlock)(void) = ^{
		@autoreleasepool
		{
			NSOpenPanel *panel = [NSOpenPanel openPanel];
			panel.canChooseFiles = YES;
			panel.canChooseDirectories = NO;
			panel.allowsMultipleSelection = NO;
			panel.treatsFilePackagesAsDirectories = NO;
			panel.allowedContentTypes = @[ UTTypeApplicationBundle ];
			panel.directoryURL = [NSURL fileURLWithPath:@"/Applications" isDirectory:YES];
			if ([panel runModal] != NSModalResponseOK || panel.URL == nil)
			{
				result = files_copy_json(@{ @"canceled": @YES, @"path": @"" });
				return;
			}
			result = files_copy_json(@{ @"canceled": @NO, @"path": panel.URL.path ?: @"" });
		}
	};

	if ([NSThread isMainThread])
	{
		pickerBlock();
	}
	else
	{
		dispatch_sync(dispatch_get_main_queue(), pickerBlock);
	}
	return result;
}

__attribute__((visibility("default"))) int files_macos_reveal_path(const char *path)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		if (url == nil)
		{
			return 0;
		}

		[[NSWorkspace sharedWorkspace] activateFileViewerSelectingURLs:@[ url ]];
		return 1;
	}
}

__attribute__((visibility("default"))) int files_macos_open_url(const char *url)
{
	@autoreleasepool
	{
		NSString *value = url == NULL ? nil : [NSString stringWithUTF8String:url];
		NSURL *targetURL = value == nil ? nil : [NSURL URLWithString:value];
		return targetURL != nil && [[NSWorkspace sharedWorkspace] openURL:targetURL] ? 1 : 0;
	}
}

__attribute__((visibility("default"))) int files_macos_full_disk_access_status(void)
{
	@autoreleasepool
	{
		NSString *home = NSHomeDirectory();
		NSString *trashPath = [home stringByAppendingPathComponent:@".Trash"];
		NSError *trashError = nil;
		if ([[NSFileManager defaultManager] contentsOfDirectoryAtPath:trashPath error:&trashError] != nil)
		{
			return 1;
		}
		if (trashError.code == NSFileReadNoPermissionError ||
			([trashError.domain isEqualToString:NSPOSIXErrorDomain] && (trashError.code == EACCES || trashError.code == EPERM)))
		{
			return 0;
		}
		NSArray<NSDictionary<NSString *, id> *> *probes = @[
			@{ @"path": [home stringByAppendingPathComponent:@"Library/Application Support/com.apple.TCC/TCC.db"], @"directory": @NO },
			@{ @"path": [home stringByAppendingPathComponent:@"Library/Safari/History.db"], @"directory": @NO },
			@{ @"path": [home stringByAppendingPathComponent:@"Library/Messages/chat.db"], @"directory": @NO },
			@{ @"path": [home stringByAppendingPathComponent:@"Library/Mail"], @"directory": @YES },
		];
		BOOL foundProbe = NO;
		for (NSDictionary<NSString *, id> *probe in probes)
		{
			NSString *path = probe[@"path"];
			BOOL isDirectory = [probe[@"directory"] boolValue];
			if (![[NSFileManager defaultManager] fileExistsAtPath:path])
			{
				continue;
			}

			foundProbe = YES;
			NSError *error = nil;
			if (isDirectory)
			{
				if ([[NSFileManager defaultManager] contentsOfDirectoryAtPath:path error:&error] != nil)
				{
					return 1;
				}
			}
			else
			{
				NSFileHandle *handle = [NSFileHandle fileHandleForReadingFromURL:[NSURL fileURLWithPath:path] error:&error];
				if (handle != nil)
				{
					[handle closeFile];
					return 1;
				}
			}
			if (error.code == NSFileReadNoPermissionError ||
				([error.domain isEqualToString:NSPOSIXErrorDomain] && (error.code == EACCES || error.code == EPERM)))
			{
				return 0;
			}
		}
		return foundProbe ? 0 : -1;
	}
}

__attribute__((visibility("default"))) int files_macos_open_full_disk_access_settings(void)
{
	@autoreleasepool
	{
		NSURL *url = [NSURL URLWithString:@"x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles"];
		return url != nil && [[NSWorkspace sharedWorkspace] openURL:url] ? 1 : 0;
	}
}

__attribute__((visibility("default"))) int files_macos_eject_volume(const char *path)
{
	@autoreleasepool
	{
		NSURL *volumeURL = files_url_from_path(path);
		if (volumeURL == nil)
		{
			return 0;
		}

		__block BOOL success = NO;
		void (^ejectBlock)(void) = ^{
			NSError *error = nil;
			success = [[NSWorkspace sharedWorkspace] unmountAndEjectDeviceAtURL:volumeURL error:&error];
		};
		if ([NSThread isMainThread])
		{
			ejectBlock();
		}
		else
		{
			dispatch_sync(dispatch_get_main_queue(), ejectBlock);
		}
		if (!success)
		{
			NSTask *task = [[NSTask alloc] init];
			task.executableURL = [NSURL fileURLWithPath:@"/usr/sbin/diskutil"];
			task.arguments = @[ @"eject", volumeURL.path ];
			task.standardOutput = [NSPipe pipe];
			task.standardError = [NSPipe pipe];
			NSError *launchError = nil;
			if ([task launchAndReturnError:&launchError])
			{
				[task waitUntilExit];
				success = task.terminationStatus == 0;
			}
		}
		return success ? 1 : 0;
	}
}

__attribute__((visibility("default"))) int files_macos_open_terminal(const char *path, const char *bundleIdentifier)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		NSString *identifier = bundleIdentifier == NULL ? @"com.apple.Terminal" : [NSString stringWithUTF8String:bundleIdentifier];
		NSWorkspace *workspace = [NSWorkspace sharedWorkspace];
		NSURL *applicationURL = [workspace URLForApplicationWithBundleIdentifier:identifier];
		if (url == nil || applicationURL == nil)
		{
			return 0;
		}

		dispatch_async(dispatch_get_main_queue(), ^{
			NSWorkspaceOpenConfiguration *configuration = [NSWorkspaceOpenConfiguration configuration];
			configuration.activates = YES;
			[workspace
				openURLs:@[ url ]
				withApplicationAtURL:applicationURL
				configuration:configuration
				completionHandler:nil];
		});
		return 1;
	}
}

__attribute__((visibility("default"))) char *files_macos_connect_server(const char *address)
{
	@autoreleasepool
	{
		NSString *addressValue = address == NULL ? nil : [NSString stringWithUTF8String:address];
		NSURL *url = addressValue == nil ? nil : [NSURL URLWithString:addressValue];
		NSSet<NSString *> *supportedSchemes = [NSSet setWithObjects:@"smb", @"afp", @"nfs", @"ftp", nil];
		NSString *scheme = url.scheme.lowercaseString;
		if (url == nil || ![supportedSchemes containsObject:scheme] || url.host.length == 0)
		{
			return files_copy_json(@{
				@"success": @NO,
				@"canceled": @NO,
				@"mountPaths": @[],
				@"openedExternally": @NO,
				@"error": @"The server address is invalid."
			});
		}

		if ([scheme isEqualToString:@"ftp"])
		{
			__block BOOL opened = NO;
			void (^openBlock)(void) = ^{
				opened = [[NSWorkspace sharedWorkspace] openURL:url];
			};
			if ([NSThread isMainThread])
			{
				openBlock();
			}
			else
			{
				dispatch_sync(dispatch_get_main_queue(), openBlock);
			}

			return files_copy_json(@{
				@"success": @(opened),
				@"canceled": @NO,
				@"mountPaths": @[],
				@"openedExternally": @(opened),
				@"error": opened ? @"" : @"The FTP server couldn't be opened."
			});
		}

		NSMutableDictionary *openOptions = [@{
			(__bridge NSString *)kNAUIOptionKey: (__bridge NSString *)kNAUIOptionAllowUI
		} mutableCopy];
		__block int status = 0;
		__block CFArrayRef mountPointsReference = NULL;
		void (^mountBlock)(void) = ^{
			status = NetFSMountURLSync(
				(__bridge CFURLRef)url,
				NULL,
				NULL,
				NULL,
				(__bridge CFMutableDictionaryRef)openOptions,
				NULL,
				&mountPointsReference);
		};
		if ([NSThread isMainThread])
		{
			mountBlock();
		}
		else
		{
			dispatch_sync(dispatch_get_main_queue(), mountBlock);
		}

		NSArray *mountPoints = CFBridgingRelease(mountPointsReference) ?: @[];
		NSMutableArray<NSString *> *paths = [NSMutableArray array];
		for (id mountPoint in mountPoints)
		{
			if ([mountPoint isKindOfClass:[NSString class]])
			{
				[paths addObject:mountPoint];
			}
			else if ([mountPoint isKindOfClass:[NSURL class]] && ((NSURL *)mountPoint).path != nil)
			{
				[paths addObject:((NSURL *)mountPoint).path];
			}
		}

		BOOL canceled = status == -128;
		NSString *error = @"";
		if (status != 0 && !canceled)
		{
			error = status > 0
				? [NSString stringWithUTF8String:strerror(status)]
				: [NSError errorWithDomain:NSOSStatusErrorDomain code:status userInfo:nil].localizedDescription;
		}
		return files_copy_json(@{
			@"success": @(status == 0),
			@"canceled": @(canceled),
			@"mountPaths": paths,
			@"openedExternally": @NO,
			@"error": error ?: @"The server couldn't be connected."
		});
	}
}

__attribute__((visibility("default"))) char *files_macos_move_to_trash(const char *path)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		if (url == nil)
		{
			return strdup("Invalid file URL.");
		}

		NSError *operationError = nil;
		BOOL succeeded = [[NSFileManager defaultManager]
			trashItemAtURL:url
			resultingItemURL:nil
			error:&operationError];
		return succeeded ? NULL : files_copy_error(operationError);
	}
}

__attribute__((visibility("default"))) char *files_macos_move_to_trash_with_result(const char *path)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		if (url == nil)
		{
			return files_copy_json(@{
				@"success": @NO,
				@"originalPath": @"",
				@"trashPath": @"",
				@"error": @"Invalid file URL."
			});
		}

		NSError *operationError = nil;
		NSURL *resultingURL = nil;
		BOOL succeeded = [[NSFileManager defaultManager]
			trashItemAtURL:url
			resultingItemURL:&resultingURL
			error:&operationError];
		if (succeeded && resultingURL != nil)
		{
			NSData *originalPath = [url.path dataUsingEncoding:NSUTF8StringEncoding];
			setxattr(resultingURL.fileSystemRepresentation, FilesTrashOriginalPathAttribute,
				originalPath.bytes, originalPath.length, 0, 0);
		}
		return files_copy_json(@{
			@"success": @(succeeded),
			@"originalPath": url.path ?: @"",
			@"trashPath": resultingURL.path ?: @"",
			@"error": operationError.localizedDescription ?: @""
		});
	}
}

__attribute__((visibility("default"))) char *files_macos_restore_from_trash(const char *path)
{
	@autoreleasepool
	{
		NSURL *trashURL = files_url_from_path(path);
		if (trashURL == nil)
		{
			return strdup("Invalid Trash item URL.");
		}

		ssize_t length = getxattr(trashURL.fileSystemRepresentation, FilesTrashOriginalPathAttribute, NULL, 0, 0, 0);
		if (length <= 0)
		{
			return strdup("The original location for this item is unavailable.");
		}
		NSMutableData *data = [NSMutableData dataWithLength:(NSUInteger)length];
		if (getxattr(trashURL.fileSystemRepresentation, FilesTrashOriginalPathAttribute,
			data.mutableBytes, data.length, 0, 0) != length)
		{
			return strdup("The original location for this item couldn't be read.");
		}

		NSString *originalPath = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
		NSURL *originalURL = originalPath.length == 0 ? nil : [NSURL fileURLWithPath:originalPath];
		if (originalURL == nil || [[NSFileManager defaultManager] fileExistsAtPath:originalURL.path])
		{
			return strdup("An item already exists at the original location.");
		}
		if (![[NSFileManager defaultManager] fileExistsAtPath:originalURL.URLByDeletingLastPathComponent.path])
		{
			return strdup("The original folder no longer exists.");
		}

		NSError *error = nil;
		if (![[NSFileManager defaultManager] moveItemAtURL:trashURL toURL:originalURL error:&error])
		{
			return files_copy_error(error);
		}
		removexattr(originalURL.fileSystemRepresentation, FilesTrashOriginalPathAttribute, 0);
		return NULL;
	}
}

__attribute__((visibility("default"))) int files_macos_preview_path(const char *path)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		if (url == nil)
		{
			return 0;
		}

		dispatch_async(dispatch_get_main_queue(), ^{
			quickLookDataSource = [FilesQuickLookDataSource new];
			quickLookDataSource.url = url;
			QLPreviewPanel *panel = [QLPreviewPanel sharedPreviewPanel];
			panel.dataSource = quickLookDataSource;
			[panel reloadData];
			[panel makeKeyAndOrderFront:nil];
		});
		return 1;
	}
}

__attribute__((visibility("default"))) char *files_macos_write_file_clipboard(const char *pathsJson, int isCut)
{
	@autoreleasepool
	{
		if (pathsJson == NULL)
		{
			return strdup("Invalid clipboard data.");
		}

		NSData *jsonData = [[NSData alloc] initWithBytes:pathsJson length:strlen(pathsJson)];
		NSError *jsonError = nil;
		id value = [NSJSONSerialization JSONObjectWithData:jsonData options:0 error:&jsonError];
		if (![value isKindOfClass:[NSArray class]])
		{
			return jsonError == nil ? strdup("Invalid clipboard data.") : files_copy_error(jsonError);
		}

		NSMutableArray<NSURL *> *urls = [NSMutableArray array];
		for (id pathValue in (NSArray *)value)
		{
			if (![pathValue isKindOfClass:[NSString class]])
			{
				return strdup("Invalid clipboard path.");
			}

			[urls addObject:[NSURL fileURLWithPath:(NSString *)pathValue]];
		}

		NSPasteboard *pasteboard = [NSPasteboard generalPasteboard];
		[pasteboard clearContents];
		if (urls.count > 0 && ![pasteboard writeObjects:urls])
		{
			return strdup("The file URLs couldn't be written to the pasteboard.");
		}

		if (isCut != 0)
		{
			[pasteboard setString:@"1" forType:FilesCutPasteboardType];
		}

		return NULL;
	}
}

__attribute__((visibility("default"))) char *files_macos_read_file_clipboard(void)
{
	@autoreleasepool
	{
		NSPasteboard *pasteboard = [NSPasteboard generalPasteboard];
		NSDictionary *options = @{ NSPasteboardURLReadingFileURLsOnlyKey: @YES };
		NSArray<NSURL *> *urls = [pasteboard readObjectsForClasses:@[ [NSURL class] ] options:options];
		NSMutableArray<NSString *> *paths = [NSMutableArray array];
		for (NSURL *url in urls)
		{
			if (url.isFileURL && url.path != nil)
			{
				[paths addObject:url.path];
			}
		}

		NSDictionary *result = @{
			@"paths": paths,
			@"isCut": [NSNumber numberWithBool:[pasteboard stringForType:FilesCutPasteboardType] != nil],
			@"changeCount": @(pasteboard.changeCount),
		};
		NSError *jsonError = nil;
		NSData *jsonData = [NSJSONSerialization dataWithJSONObject:result options:0 error:&jsonError];
		if (jsonData == nil)
		{
			return NULL;
		}

		NSString *json = [[NSString alloc] initWithData:jsonData encoding:NSUTF8StringEncoding];
		return files_copy_utf8_string(json);
	}
}

__attribute__((visibility("default"))) int files_macos_clear_file_clipboard(long expectedChangeCount)
{
	@autoreleasepool
	{
		NSPasteboard *pasteboard = [NSPasteboard generalPasteboard];
		if (pasteboard.changeCount != expectedChangeCount)
		{
			return 0;
		}

		[pasteboard clearContents];
		return 1;
	}
}

__attribute__((visibility("default"))) char *files_macos_copy_metadata(const char *sourcePath, const char *destinationPath)
{
	if (sourcePath == NULL || destinationPath == NULL)
	{
		return strdup("Invalid metadata path.");
	}

	copyfile_flags_t flags = COPYFILE_ACL | COPYFILE_XATTR;
	struct stat sourceStatus;
	if (lstat(sourcePath, &sourceStatus) == 0 && S_ISLNK(sourceStatus.st_mode))
	{
		flags |= COPYFILE_NOFOLLOW;
	}

	if (copyfile(sourcePath, destinationPath, NULL, flags) == 0)
	{
		return NULL;
	}

	int errorNumber = errno;
	const char *message = strerror(errorNumber);
	return strdup(message == NULL ? "The file metadata couldn't be copied." : message);
}

__attribute__((visibility("default"))) char *files_macos_get_finder_tags(const char *path)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		if (url == nil)
		{
			return NULL;
		}

		NSArray<NSString *> *tags = nil;
		if (![url getResourceValue:&tags forKey:NSURLTagNamesKey error:nil])
		{
			return NULL;
		}

		return files_copy_json(tags ?: @[]);
	}
}

__attribute__((visibility("default"))) char *files_macos_get_file_sort_metadata(const char *path)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		if (url == nil)
		{
			return NULL;
		}

		NSDate *addedDate = nil;
		NSDate *lastOpenedDate = nil;
		NSString *kind = nil;
		NSArray<NSString *> *tags = nil;
		[url getResourceValue:&addedDate forKey:NSURLAddedToDirectoryDateKey error:nil];
		[url getResourceValue:&lastOpenedDate forKey:NSURLContentAccessDateKey error:nil];
		[url getResourceValue:&kind forKey:NSURLLocalizedTypeDescriptionKey error:nil];
		[url getResourceValue:&tags forKey:NSURLTagNamesKey error:nil];

		NSString *version = @"";
		NSBundle *bundle = [NSBundle bundleWithURL:url];
		id versionValue = [bundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"];
		if ([versionValue isKindOfClass:NSString.class])
		{
			version = versionValue;
		}

		NSString *comments = @"";
		MDItemRef metadataItem = MDItemCreate(kCFAllocatorDefault, (__bridge CFStringRef)url.path);
		if (metadataItem != NULL)
		{
			id commentValue = CFBridgingRelease(MDItemCopyAttribute(metadataItem, kMDItemFinderComment));
			if ([commentValue isKindOfClass:NSString.class])
			{
				comments = commentValue;
			}
			CFRelease(metadataItem);
		}

		NSDictionary *result = @{
			@"AddedUnixSeconds": addedDate == nil ? NSNull.null : @(addedDate.timeIntervalSince1970),
			@"LastOpenedUnixSeconds": lastOpenedDate == nil ? NSNull.null : @(lastOpenedDate.timeIntervalSince1970),
			@"Kind": kind ?: @"",
			@"Version": version,
			@"Comments": comments,
			@"Tags": tags ?: @[],
		};
		return files_copy_json(result);
	}
}

__attribute__((visibility("default"))) char *files_macos_set_finder_tags(const char *path, const char *tagsJson)
{
	@autoreleasepool
	{
		NSURL *url = files_url_from_path(path);
		if (url == nil || tagsJson == NULL)
		{
			return strdup("The Finder tag request is invalid.");
		}

		NSData *jsonData = [[NSData alloc] initWithBytes:tagsJson length:strlen(tagsJson)];
		NSError *jsonError = nil;
		id value = [NSJSONSerialization JSONObjectWithData:jsonData options:0 error:&jsonError];
		if (![value isKindOfClass:NSArray.class])
		{
			return files_copy_error(jsonError) ?: strdup("Finder tags must be a JSON array.");
		}
		for (id tag in (NSArray *)value)
		{
			if (![tag isKindOfClass:NSString.class])
			{
				return strdup("Every Finder tag must be text.");
			}
		}

		NSError *error = nil;
		if (![url setResourceValue:value forKey:NSURLTagNamesKey error:&error])
		{
			return files_copy_error(error) ?: strdup("Finder tags couldn't be saved.");
		}
		return NULL;
	}
}

__attribute__((visibility("default"))) char *files_macos_get_file_security(const char *path)
{
	@autoreleasepool
	{
		if (path == NULL)
		{
			return NULL;
		}

		struct stat status;
		if (stat(path, &status) != 0)
		{
			return NULL;
		}
		struct passwd *user = getpwuid(status.st_uid);
		struct group *group = getgrgid(status.st_gid);
		NSString *ownerName = user == NULL || user->pw_name == NULL
			? [NSString stringWithFormat:@"%u", status.st_uid]
			: [NSString stringWithUTF8String:user->pw_name];
		NSString *groupName = group == NULL || group->gr_name == NULL
			? [NSString stringWithFormat:@"%u", status.st_gid]
			: [NSString stringWithUTF8String:group->gr_name];

		NSString *aclText = @"";
		acl_t acl = acl_get_file(path, ACL_TYPE_EXTENDED);
		if (acl != NULL)
		{
			ssize_t textLength = 0;
			char *text = acl_to_text(acl, &textLength);
			if (text != NULL)
			{
				aclText = [[NSString alloc] initWithBytes:text length:(NSUInteger)(textLength > 0 ? textLength : 0) encoding:NSUTF8StringEncoding] ?: @"";
				acl_free(text);
			}
			acl_free(acl);
		}

		return files_copy_json(@{
			@"owner": ownerName ?: @"",
			@"group": groupName ?: @"",
			@"userId": @(status.st_uid),
			@"groupId": @(status.st_gid),
			@"acl": aclText,
			@"isHidden": (status.st_flags & UF_HIDDEN) != 0 ? @YES : @NO,
			@"isLocked": (status.st_flags & UF_IMMUTABLE) != 0 ? @YES : @NO
		});
	}
}

__attribute__((visibility("default"))) char *files_macos_set_file_flags(
	const char *path,
	int isHidden,
	int isLocked)
{
	@autoreleasepool
	{
		if (path == NULL)
		{
			return strdup("The file flag request is invalid.");
		}

		struct stat status;
		if (stat(path, &status) != 0)
		{
			return strdup(strerror(errno));
		}
		u_int flags = status.st_flags;
		flags = isHidden ? flags | UF_HIDDEN : flags & ~UF_HIDDEN;
		flags = isLocked ? flags | UF_IMMUTABLE : flags & ~UF_IMMUTABLE;
		if (chflags(path, flags) != 0)
		{
			return strdup(strerror(errno));
		}
		return NULL;
	}
}

static int files_macos_resolve_user(const char *value, uid_t *userId)
{
	if (value == NULL || value[0] == '\0')
	{
		return EINVAL;
	}
	char *end = NULL;
	errno = 0;
	unsigned long numericId = strtoul(value, &end, 10);
	if (errno == 0 && end != value && *end == '\0' && numericId <= UINT_MAX)
	{
		*userId = (uid_t)numericId;
		return 0;
	}
	struct passwd *user = getpwnam(value);
	if (user == NULL)
	{
		return ENOENT;
	}
	*userId = user->pw_uid;
	return 0;
}

static int files_macos_resolve_group(const char *value, gid_t *groupId)
{
	if (value == NULL || value[0] == '\0')
	{
		return EINVAL;
	}
	char *end = NULL;
	errno = 0;
	unsigned long numericId = strtoul(value, &end, 10);
	if (errno == 0 && end != value && *end == '\0' && numericId <= UINT_MAX)
	{
		*groupId = (gid_t)numericId;
		return 0;
	}
	struct group *group = getgrnam(value);
	if (group == NULL)
	{
		return ENOENT;
	}
	*groupId = group->gr_gid;
	return 0;
}

__attribute__((visibility("default"))) char *files_macos_set_file_security(
	const char *path,
	const char *owner,
	const char *group,
	const char *aclText)
{
	@autoreleasepool
	{
		if (path == NULL || aclText == NULL)
		{
			return strdup("The file security request is invalid.");
		}

		uid_t userId = 0;
		gid_t groupId = 0;
		if (files_macos_resolve_user(owner, &userId) != 0)
		{
			return strdup("The owner name or ID couldn't be resolved.");
		}
		if (files_macos_resolve_group(group, &groupId) != 0)
		{
			return strdup("The group name or ID couldn't be resolved.");
		}

		acl_t acl = aclText[0] == '\0' ? acl_init(0) : acl_from_text(aclText);
		if (acl == NULL || acl_valid(acl) != 0)
		{
			if (acl != NULL)
			{
				acl_free(acl);
			}
			return strdup("The access control list format is invalid.");
		}

		struct stat status;
		if (stat(path, &status) != 0)
		{
			acl_free(acl);
			return strdup(strerror(errno));
		}
		if ((status.st_uid != userId || status.st_gid != groupId) && chown(path, userId, groupId) != 0)
		{
			acl_free(acl);
			return strdup(strerror(errno));
		}
		if (acl_set_file(path, ACL_TYPE_EXTENDED, acl) != 0)
		{
			acl_free(acl);
			return strdup(strerror(errno));
		}
		acl_free(acl);
		return NULL;
	}
}

__attribute__((visibility("default"))) char *files_macos_coordinate_file_operation(
	const char *sourcePath,
	const char *destinationPath,
	int isMove,
	FilesCoordinatedOperation operation,
	void *operationContext)
{
	@autoreleasepool
	{
		NSURL *sourceURL = files_url_from_path(sourcePath);
		NSURL *destinationURL = files_url_from_path(destinationPath);
		if (sourceURL == nil || destinationURL == nil || operation == NULL)
		{
			return strdup("The coordinated file operation is invalid.");
		}

		NSFileCoordinator *coordinator = [[NSFileCoordinator alloc] initWithFilePresenter:nil];
		coordinator.purposeIdentifier = @"io.filescommunity.files.macos.transfer";
		NSFileCoordinatorWritingOptions destinationOptions = [[NSFileManager defaultManager] fileExistsAtPath:destinationURL.path]
			? NSFileCoordinatorWritingForReplacing
			: 0;
		__block BOOL invoked = NO;
		__block int operationResult = 0;
		NSError *coordinationError = nil;

		if (isMove != 0)
		{
			[coordinator
				coordinateWritingItemAtURL:sourceURL
				options:NSFileCoordinatorWritingForMoving
				writingItemAtURL:destinationURL
				options:destinationOptions
				error:&coordinationError
				byAccessor:^(NSURL *coordinatedSourceURL, NSURL *coordinatedDestinationURL) {
					invoked = YES;
					operationResult = operation(
						operationContext,
						coordinatedSourceURL.path.UTF8String,
						coordinatedDestinationURL.path.UTF8String);
				}];
		}
		else
		{
			[coordinator
				coordinateReadingItemAtURL:sourceURL
				options:NSFileCoordinatorReadingWithoutChanges
				writingItemAtURL:destinationURL
				options:destinationOptions
				error:&coordinationError
				byAccessor:^(NSURL *coordinatedSourceURL, NSURL *coordinatedDestinationURL) {
					invoked = YES;
					operationResult = operation(
						operationContext,
						coordinatedSourceURL.path.UTF8String,
						coordinatedDestinationURL.path.UTF8String);
				}];
		}

		if (coordinationError != nil)
		{
			return files_copy_error(coordinationError);
		}
		if (!invoked)
		{
			return strdup("The coordinated file operation wasn't invoked.");
		}
		return operationResult != 0 ? NULL : strdup("The coordinated file operation failed.");
	}
}

__attribute__((visibility("default"))) void *files_macos_spotlight_create(void)
{
	FilesSpotlightSearchContext *context = calloc(1, sizeof(FilesSpotlightSearchContext));
	if (context != NULL)
	{
		atomic_init(&context->cancelled, false);
	}
	return context;
}

__attribute__((visibility("default"))) void files_macos_spotlight_cancel(void *searchContext)
{
	FilesSpotlightSearchContext *context = searchContext;
	if (context != NULL)
	{
		atomic_store(&context->cancelled, true);
	}
}

__attribute__((visibility("default"))) char *files_macos_spotlight_search(
	void *searchContext,
	const char *rootPath,
	const char *queryJson,
	int includeHidden,
	int timeoutMilliseconds)
{
	@autoreleasepool
	{
		FilesSpotlightSearchContext *context = searchContext;
		NSURL *rootURL = files_url_from_path(rootPath);
		NSString *queryText = queryJson == NULL ? nil : [NSString stringWithUTF8String:queryJson];
		NSData *queryData = [queryText dataUsingEncoding:NSUTF8StringEncoding];
		NSDictionary *queryPlan = queryData == nil
			? nil
			: [NSJSONSerialization JSONObjectWithData:queryData options:0 error:nil];
		if (context == NULL || rootURL == nil || ![queryPlan isKindOfClass:NSDictionary.class] || atomic_load(&context->cancelled))
		{
			return NULL;
		}

		NSMetadataQuery *query = [NSMetadataQuery new];
		query.searchScopes = @[ rootURL ];
		NSMutableArray<NSPredicate *> *predicates = [NSMutableArray array];
		NSArray *terms = queryPlan[@"terms"];
		if ([terms isKindOfClass:NSArray.class])
		{
			for (NSString *term in terms)
			{
				if (![term isKindOfClass:NSString.class] || term.length == 0)
				{
					continue;
				}
				[predicates addObject:[NSPredicate predicateWithFormat:
					@"(%K CONTAINS[cd] %@) OR (%K CONTAINS[cd] %@)",
					NSMetadataItemFSNameKey,
					term,
					FilesMetadataItemTextContentKey,
					term]];
			}
		}

		NSArray *extensions = queryPlan[@"extensions"];
		if ([extensions isKindOfClass:NSArray.class] && extensions.count > 0)
		{
			NSMutableArray<NSPredicate *> *extensionPredicates = [NSMutableArray array];
			for (NSString *extension in extensions)
			{
				if ([extension isKindOfClass:NSString.class] && extension.length > 0)
				{
					[extensionPredicates addObject:[NSPredicate predicateWithFormat:
						@"%K ENDSWITH[cd] %@",
						NSMetadataItemFSNameKey,
						[@"." stringByAppendingString:extension]]];
				}
			}
			if (extensionPredicates.count > 0)
			{
				[predicates addObject:extensionPredicates.count == 1
					? extensionPredicates.firstObject
					: [NSCompoundPredicate orPredicateWithSubpredicates:extensionPredicates]];
			}
		}

		NSArray *kinds = queryPlan[@"kinds"];
		if ([kinds isKindOfClass:NSArray.class] && kinds.count > 0)
		{
			NSMutableArray<NSPredicate *> *kindPredicates = [NSMutableArray array];
			if ([kinds containsObject:@"file"])
			{
				[kindPredicates addObject:[NSPredicate predicateWithFormat:
					@"NOT (%K CONTAINS %@)", FilesMetadataItemContentTypeTreeKey, @"public.folder"]];
			}
			if ([kinds containsObject:@"folder"])
			{
				[kindPredicates addObject:[NSPredicate predicateWithFormat:
					@"%K CONTAINS %@", FilesMetadataItemContentTypeTreeKey, @"public.folder"]];
			}

			NSDictionary<NSString *, NSArray<NSString *> *> *kindTypes = @{
				@"image": @[ @"public.image" ],
				@"audio": @[ @"public.audio" ],
				@"video": @[ @"public.movie" ],
				@"document": @[ @"public.text", @"public.composite-content", @"com.adobe.pdf" ],
				@"archive": @[ @"public.archive" ],
			};
			for (NSString *kind in kinds)
			{
				for (NSString *contentType in kindTypes[kind] ?: @[])
				{
					[kindPredicates addObject:[NSPredicate predicateWithFormat:
						@"%K CONTAINS %@", FilesMetadataItemContentTypeTreeKey, contentType]];
				}
			}
			if (kindPredicates.count > 0)
			{
				[predicates addObject:kindPredicates.count == 1
					? kindPredicates.firstObject
					: [NSCompoundPredicate orPredicateWithSubpredicates:kindPredicates]];
			}
		}

		NSArray *tagGroups = queryPlan[@"tagGroups"];
		if ([tagGroups isKindOfClass:NSArray.class])
		{
			for (NSArray *tagGroup in tagGroups)
			{
				if (![tagGroup isKindOfClass:NSArray.class])
				{
					continue;
				}

				NSMutableArray<NSPredicate *> *tagPredicates = [NSMutableArray array];
				for (NSString *tag in tagGroup)
				{
					if ([tag isKindOfClass:NSString.class] && tag.length > 0)
					{
						[tagPredicates addObject:[NSPredicate predicateWithFormat:
							@"ANY %K BEGINSWITH[cd] %@", FilesMetadataItemUserTagsKey, tag]];
					}
				}
				if (tagPredicates.count > 0)
				{
					[predicates addObject:tagPredicates.count == 1
						? tagPredicates.firstObject
						: [NSCompoundPredicate orPredicateWithSubpredicates:tagPredicates]];
				}
			}
		}

		NSNumber *minimumSize = queryPlan[@"minimumSize"];
		if ([minimumSize isKindOfClass:NSNumber.class])
		{
			BOOL inclusive = [queryPlan[@"minimumSizeInclusive"] boolValue];
			[predicates addObject:[NSPredicate predicateWithFormat:
				inclusive ? @"%K >= %@" : @"%K > %@", FilesMetadataItemFSSizeKey, minimumSize]];
		}
		NSNumber *maximumSize = queryPlan[@"maximumSize"];
		if ([maximumSize isKindOfClass:NSNumber.class])
		{
			BOOL inclusive = [queryPlan[@"maximumSizeInclusive"] boolValue];
			[predicates addObject:[NSPredicate predicateWithFormat:
				inclusive ? @"%K <= %@" : @"%K < %@", FilesMetadataItemFSSizeKey, maximumSize]];
		}

		NSNumber *modifiedAfter = queryPlan[@"modifiedAfter"];
		if ([modifiedAfter isKindOfClass:NSNumber.class])
		{
			BOOL inclusive = [queryPlan[@"modifiedAfterInclusive"] boolValue];
			NSDate *date = [NSDate dateWithTimeIntervalSince1970:modifiedAfter.doubleValue];
			[predicates addObject:[NSPredicate predicateWithFormat:
				inclusive ? @"%K >= %@" : @"%K > %@", FilesMetadataItemFSContentChangeDateKey, date]];
		}
		NSNumber *modifiedBefore = queryPlan[@"modifiedBefore"];
		if ([modifiedBefore isKindOfClass:NSNumber.class])
		{
			BOOL inclusive = [queryPlan[@"modifiedBeforeInclusive"] boolValue];
			NSDate *date = [NSDate dateWithTimeIntervalSince1970:modifiedBefore.doubleValue];
			[predicates addObject:[NSPredicate predicateWithFormat:
				inclusive ? @"%K <= %@" : @"%K < %@", FilesMetadataItemFSContentChangeDateKey, date]];
		}

		query.predicate = predicates.count == 0
			? [NSPredicate predicateWithFormat:@"%K LIKE[cd] %@", NSMetadataItemFSNameKey, @"*"]
			: predicates.count == 1
				? predicates.firstObject
				: [NSCompoundPredicate andPredicateWithSubpredicates:predicates];
		query.valueListAttributes = @[ NSMetadataItemPathKey, FilesMetadataItemFSInvisibleKey ];

		__block BOOL finished = NO;
		id observer = [[NSNotificationCenter defaultCenter]
			addObserverForName:NSMetadataQueryDidFinishGatheringNotification
			object:query
			queue:nil
			usingBlock:^(NSNotification *notification) {
				finished = YES;
			}];

		BOOL started = [query startQuery];
		NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:MAX(1, timeoutMilliseconds) / 1000.0];
		while (started && !finished && !atomic_load(&context->cancelled) && deadline.timeIntervalSinceNow > 0)
		{
			@autoreleasepool
			{
				[[NSRunLoop currentRunLoop]
					runMode:NSDefaultRunLoopMode
					beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
			}
		}

		BOOL cancelled = atomic_load(&context->cancelled);
		BOOL succeeded = started && finished && !cancelled;
		NSMutableArray<NSString *> *paths = [NSMutableArray array];
		if (succeeded)
		{
			[query disableUpdates];
			NSString *normalizedRoot = rootURL.path.stringByStandardizingPath;
			NSString *rootPrefix = [normalizedRoot stringByAppendingString:@"/"];
			for (NSMetadataItem *item in query.results)
			{
				NSString *path = [item valueForAttribute:NSMetadataItemPathKey];
				NSNumber *isInvisible = [item valueForAttribute:FilesMetadataItemFSInvisibleKey];
				if (path.length == 0 || ![path hasPrefix:rootPrefix])
				{
					continue;
				}

				if (includeHidden == 0)
				{
					NSString *relativePath = [path substringFromIndex:rootPrefix.length];
					BOOL hasHiddenComponent = [relativePath.pathComponents indexOfObjectPassingTest:^BOOL(NSString *component, NSUInteger index, BOOL *stop) {
						return [component hasPrefix:@"."];
					}] != NSNotFound;
					if (isInvisible.boolValue || hasHiddenComponent)
					{
						continue;
					}
				}

				[paths addObject:path];
			}
		}

		[query stopQuery];
		[[NSNotificationCenter defaultCenter] removeObserver:observer];
		if (!succeeded)
		{
			return NULL;
		}

		NSError *jsonError = nil;
		NSData *jsonData = [NSJSONSerialization dataWithJSONObject:paths options:0 error:&jsonError];
		if (jsonData == nil)
		{
			return NULL;
		}

		NSString *json = [[NSString alloc] initWithData:jsonData encoding:NSUTF8StringEncoding];
		return files_copy_utf8_string(json);
	}
}

__attribute__((visibility("default"))) char *files_macos_share_files(const char *pathsJson)
{
	@autoreleasepool
	{
		if (pathsJson == NULL)
		{
			return strdup("Invalid sharing data.");
		}

		NSData *jsonData = [[NSData alloc] initWithBytes:pathsJson length:strlen(pathsJson)];
		NSError *jsonError = nil;
		id value = [NSJSONSerialization JSONObjectWithData:jsonData options:0 error:&jsonError];
		if (![value isKindOfClass:[NSArray class]])
		{
			return jsonError == nil ? strdup("Invalid sharing data.") : files_copy_error(jsonError);
		}

		NSMutableArray<NSURL *> *urls = [NSMutableArray array];
		for (id pathValue in (NSArray *)value)
		{
			if (![pathValue isKindOfClass:[NSString class]])
			{
				return strdup("Invalid sharing path.");
			}
			[urls addObject:[NSURL fileURLWithPath:(NSString *)pathValue]];
		}

		if (urls.count == 0)
		{
			return strdup("No files were selected for sharing.");
		}

		__block char *result = NULL;
		dispatch_sync(dispatch_get_main_queue(), ^{
			NSWindow *window = NSApp.keyWindow ?: NSApp.mainWindow;
			NSView *view = window.contentView;
			if (view == nil)
			{
				result = strdup("The sharing window isn't available.");
				return;
			}

			sharingServicePicker = [[NSSharingServicePicker alloc] initWithItems:urls];
			[sharingServicePicker showRelativeToRect:view.bounds ofView:view preferredEdge:NSRectEdgeMaxY];
		});
		return result;
	}
}

__attribute__((visibility("default"))) char *files_macos_share_files_via_airdrop(const char *pathsJson)
{
	@autoreleasepool
	{
		if (pathsJson == NULL)
		{
			return strdup("Invalid AirDrop data.");
		}

		NSData *jsonData = [[NSData alloc] initWithBytes:pathsJson length:strlen(pathsJson)];
		id value = [NSJSONSerialization JSONObjectWithData:jsonData options:0 error:nil];
		if (![value isKindOfClass:NSArray.class])
		{
			return strdup("Invalid AirDrop data.");
		}

		NSMutableArray<NSURL *> *urls = [NSMutableArray array];
		for (id pathValue in (NSArray *)value)
		{
			if (![pathValue isKindOfClass:NSString.class])
			{
				return strdup("Invalid AirDrop path.");
			}
			[urls addObject:[NSURL fileURLWithPath:pathValue]];
		}
		if (urls.count == 0)
		{
			return strdup("No files were selected for AirDrop.");
		}

		__block char *result = NULL;
		dispatch_sync(dispatch_get_main_queue(), ^{
			NSSharingService *service = [NSSharingService sharingServiceNamed:NSSharingServiceNameSendViaAirDrop];
			if (service == nil || ![service canPerformWithItems:urls])
			{
				result = strdup("AirDrop isn't available for the selected items.");
				return;
			}
			[service performWithItems:urls];
		});
		return result;
	}
}

static NSURL *files_macos_resolve_alias_url(NSURL *url, BOOL *wasAlias)
{
	*wasAlias = NO;
	NSNumber *isAlias = nil;
	NSError *resourceError = nil;
	if (![url getResourceValue:&isAlias forKey:NSURLIsAliasFileKey error:&resourceError] || !isAlias.boolValue)
	{
		return url;
	}

	*wasAlias = YES;
	NSError *resolutionError = nil;
	NSURL *resolvedURL = [NSURL
		URLByResolvingAliasFileAtURL:url
		options:NSURLBookmarkResolutionWithoutUI | NSURLBookmarkResolutionWithoutMounting
		error:&resolutionError];
	return resolvedURL ?: url;
}

static NSData *files_macos_png_for_file_icon(NSURL *url, double width, double height, double scale)
{
	NSImage *icon = [[NSWorkspace sharedWorkspace] iconForFile:url.path];
	if (icon == nil)
	{
		return nil;
	}

	NSInteger pixelsWide = MAX(1, (NSInteger)ceil(width * scale));
	NSInteger pixelsHigh = MAX(1, (NSInteger)ceil(height * scale));
	NSBitmapImageRep *bitmap = [[NSBitmapImageRep alloc]
		initWithBitmapDataPlanes:NULL
		pixelsWide:pixelsWide
		pixelsHigh:pixelsHigh
		bitsPerSample:8
		samplesPerPixel:4
		hasAlpha:YES
		isPlanar:NO
		colorSpaceName:NSCalibratedRGBColorSpace
		bitmapFormat:0
		bytesPerRow:0
		bitsPerPixel:0];
	if (bitmap == nil)
	{
		return nil;
	}

	bitmap.size = NSMakeSize(width, height);
	NSGraphicsContext *context = [NSGraphicsContext graphicsContextWithBitmapImageRep:bitmap];
	if (context == nil)
	{
		return nil;
	}
	[NSGraphicsContext saveGraphicsState];
	[NSGraphicsContext setCurrentContext:context];
	context.imageInterpolation = NSImageInterpolationHigh;
	[icon
		drawInRect:NSMakeRect(0, 0, width, height)
		fromRect:NSZeroRect
		operation:NSCompositingOperationCopy
		fraction:1.0];
	[context flushGraphics];
	[NSGraphicsContext restoreGraphicsState];
	return [bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
}

__attribute__((visibility("default"))) int files_macos_create_alias_file(const char *targetPath, const char *aliasPath)
{
	@autoreleasepool
	{
		NSURL *targetURL = files_url_from_path(targetPath);
		NSURL *aliasURL = files_url_from_path(aliasPath);
		if (targetURL == nil || aliasURL == nil)
		{
			return 0;
		}

		NSError *bookmarkError = nil;
		NSData *bookmarkData = [targetURL
			bookmarkDataWithOptions:NSURLBookmarkCreationSuitableForBookmarkFile
			includingResourceValuesForKeys:nil
			relativeToURL:nil
			error:&bookmarkError];
		if (bookmarkData == nil)
		{
			return 0;
		}

		NSError *writeError = nil;
		return [NSURL writeBookmarkData:bookmarkData toURL:aliasURL options:0 error:&writeError] ? 1 : 0;
	}
}

__attribute__((visibility("default"))) int files_macos_generate_thumbnail(
	const char *path,
	double width,
	double height,
	double scale,
	unsigned char **output,
	size_t *outputLength)
{
	@autoreleasepool
	{
		if (output == NULL || outputLength == NULL)
		{
			return 0;
		}

		*output = NULL;
		*outputLength = 0;
		NSURL *url = files_url_from_path(path);
		if (url == nil)
		{
			return 0;
		}

		BOOL wasAlias = NO;
		NSURL *previewURL = files_macos_resolve_alias_url(url, &wasAlias);
		NSData *iconData = files_macos_png_for_file_icon(previewURL, width, height, scale);
		if (iconData.length > 0)
		{
			unsigned char *buffer = malloc(iconData.length);
			if (buffer == NULL)
			{
				return 0;
			}

			memcpy(buffer, iconData.bytes, iconData.length);
			*output = buffer;
			*outputLength = iconData.length;
			return 1;
		}

		dispatch_semaphore_t completion = dispatch_semaphore_create(0);
		__block NSData *pngData = nil;
		QLThumbnailGenerationRequest *request = [[QLThumbnailGenerationRequest alloc]
			initWithFileAtURL:previewURL
			size:NSMakeSize(width, height)
			scale:scale
			representationTypes:QLThumbnailGenerationRequestRepresentationTypeAll];

		[[QLThumbnailGenerator sharedGenerator]
			generateBestRepresentationForRequest:request
			completionHandler:^(QLThumbnailRepresentation *representation, NSError *error) {
				if (representation != nil && error == nil)
				{
					NSBitmapImageRep *bitmap = [[NSBitmapImageRep alloc] initWithCGImage:representation.CGImage];
					pngData = [bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
				}
				dispatch_semaphore_signal(completion);
			}];

		if (dispatch_semaphore_wait(completion, dispatch_time(DISPATCH_TIME_NOW, 30 * NSEC_PER_SEC)) != 0 || pngData.length == 0)
		{
			return 0;
		}

		unsigned char *buffer = malloc(pngData.length);
		if (buffer == NULL)
		{
			return 0;
		}

		memcpy(buffer, pngData.bytes, pngData.length);
		*output = buffer;
		*outputLength = pngData.length;
		return 1;
	}
}

__attribute__((visibility("default"))) void files_macos_free(void *pointer)
{
	free(pointer);
}
