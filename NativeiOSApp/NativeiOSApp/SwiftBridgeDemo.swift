//
//  SwiftBridgeDemo.swift
//  NativeiOSApp
//
//  Created by Jonathan Thorpe on 01/06/2023.
//  Copyright © 2023 unity. All rights reserved.
//

import Foundation
import UIKit
import SwiftAsyncBridge
import OSLog

extension Logger {
    private static var subsystem = Bundle.main.bundleIdentifier!
    static let bridge = Logger(subsystem: subsystem, category: "swift_bridge_demo")
}

private enum ImplementationError : Error {
    case tooBig
}

@objc public class SwiftBridgeDemo : NSObject {
    
    private static let testSeparator = "\n-------------------\n"
    
    private let bridge : Bridge
    private let workflowPerformer : BridgeWorkflowPerformer
    private let workflowRegister : BridgeWorkflowRegister
    private var counter = 0
    
    var incrementedCounter : Int {
        counter += 1
        return counter
    }
    
    private enum Paths {
        static let startTest = "/test/start"
    }
    
    private enum Procedures {
        static let immediateGreeting = "/greeting/immediate"
        static let delayedGreeting = "/greeting/delayed"
        static let errorGreeting = "/greeting/error"
    }
    
    public override init() {
        let messenger = UnityBridgeMessenger(gameObject: "Bridge", method: "OnBridgeMessage")
        let listener = DefaultBridgeListener()
        bridge = Bridge(messenger: messenger, listener: listener)
        workflowPerformer = BridgeWorkflowPerformer(bridge: bridge)
        workflowRegister = BridgeWorkflowRegister(bridge: bridge)
        super.init()
        registerImplementations()
    }
    
    private func registerImplementations() {
        do {
            try workflowRegister.register(procedure: Procedures.delayedGreeting) { (payload : TestPayload) in
                try await Task.sleep(nanoseconds: UInt64(payload.duration * Double(NSEC_PER_SEC)))
                return TestResult(message: "Hello \(payload.name)", processed: payload.number + 200)
            }
            try workflowRegister.register(procedure: Procedures.immediateGreeting) { (payload : TestPayload) in
                return TestResult(message: "Hello \(payload.name)", processed: payload.number + 100)
            }
            try workflowRegister.register(TestResult.self, procedure: Procedures.errorGreeting) { (payload : TestPayload) in
                try await Task.sleep(nanoseconds: UInt64(payload.duration * Double(NSEC_PER_SEC)))
                throw ImplementationError.tooBig
            }
        } catch {
            Logger.bridge.error("SwiftBridgeDemo error registering implementations \(error)")
        }
    }
    
    private func sleep(seconds: Double) async throws {
        try await Task.sleep(nanoseconds: UInt64(seconds * Double(NSEC_PER_SEC)))
    }
    
    @objc public func start() {
        Task {
            try await runAll()
            try bridge.send(path: Paths.startTest, content: "")
        }
    }
    
    private func runAll() async throws {
        print(SwiftBridgeDemo.testSeparator)
        try await testImmediateWorkflow()
        print(SwiftBridgeDemo.testSeparator)
        try await sleep(seconds: 1)
        try await testDelayedWorkflow()
        print(SwiftBridgeDemo.testSeparator)
        try await sleep(seconds: 1)
        try await testConcurrentWorkflow()
        print(SwiftBridgeDemo.testSeparator)
        try await sleep(seconds: 1)
        try await testCancelledWorkflow()
        print(SwiftBridgeDemo.testSeparator)
        try await sleep(seconds: 1)
        try await testErrorWorkflow()
        print(SwiftBridgeDemo.testSeparator)
        try await sleep(seconds: 1)
    }
    
    private func testImmediateWorkflow() async throws {
        
        let payload = TestPayload(name: "Brigitte", number: incrementedCounter, duration: 5)
        Logger.bridge.log("SwiftBridgeDemo testImmediateWorkflow start \(String(describing: payload))")
        let result : TestResult = try await workflowPerformer.perform(procedure: Procedures.immediateGreeting, payload: payload)
        Logger.bridge.log("SwiftBridgeDemo testImmediateWorkflow result \(String(describing: result))")
    }
    
    private func testDelayedWorkflow() async throws {
        let payload = TestPayload(name: "Brigitte", number: incrementedCounter, duration: 5)
        Logger.bridge.log("SwiftBridgeDemo testDelayedWorkflow start \(String(describing: payload))")
        let result : TestResult = try await workflowPerformer.perform(procedure: Procedures.delayedGreeting, payload: payload)
        Logger.bridge.log("SwiftBridgeDemo testDelayedWorkflow result \(String(describing: result))")
    }
    
    private func testConcurrentWorkflow() async throws {
        let payload1 = TestPayload(name: "Brigitte", number: incrementedCounter, duration: 3)
        let payload2 = TestPayload(name: "Roger", number: incrementedCounter, duration: 6)
        let payload3 = TestPayload(name: "Marguerite", number: incrementedCounter, duration: 1)
        Logger.bridge.log("SwiftBridgeDemo testConcurrentWorkflow start \(String(describing: payload1)) \(String(describing: payload2)) \(String(describing: payload3))")
        async let task1 = workflowPerformer.perform(TestResult.self, procedure: Procedures.delayedGreeting, payload: payload1)
        async let task2 = workflowPerformer.perform(TestResult.self, procedure: Procedures.delayedGreeting, payload: payload2)
        async let task3 = workflowPerformer.perform(TestResult.self, procedure: Procedures.delayedGreeting, payload: payload3)
        let result1 : TestResult = try await task1
        let result2 : TestResult = try await task2
        let result3 : TestResult = try await task3
        Logger.bridge.log("SwiftBridgeDemo testConcurrentWorkflow result \(String(describing: result1)) \(String(describing: result2)) \(String(describing: result3))")
    }
    
    private func testCancelledWorkflow() async throws {
        let payload = TestPayload(name: "Brigitte", number: incrementedCounter, duration: 5)
        do {
            Logger.bridge.log("SwiftBridgeDemo testCancelledWorkflow start")
            // note cancellation requires Task or TaskGroup
            // https://www.hackingwithswift.com/quick-start/concurrency/how-to-cancel-a-task
            let task = Task { () -> TestResult in
                return try await workflowPerformer.perform(procedure: Procedures.delayedGreeting, payload: payload)
            }
            try await Task.sleep(nanoseconds: UInt64(3 * Double(NSEC_PER_SEC)))
            task.cancel()
            let result = try await task.value
            Logger.bridge.log("SwiftBridgeDemo testCancelledWorkflow got unexpected result \(String(describing: result))")
        } catch is CancellationError {
            Logger.bridge.log("SwiftBridgeDemo testCancelledWorkflow got expected cancellation error")
        } catch {
            Logger.bridge.error("SwiftBridgeDemo testCancelledWorkflow got unexpected error \(String(describing: error))")
        }
    }
    
    private func testErrorWorkflow() async throws {
        do {
            let payload = TestPayload(name: "Brigitte", number: incrementedCounter, duration: 5)
            Logger.bridge.log("SwiftBridgeDemo testErrorWorkflow start")
            let result : TestResult = try await workflowPerformer.perform(procedure: Procedures.errorGreeting, payload: payload)
            Logger.bridge.log("SwiftBridgeDemo testErrorWorkflow unexpected result \(String(describing: result))")
        } catch {
            Logger.bridge.log("SwiftBridgeDemo testErrorWorkflow got expected error \(String(describing: error))")
        }
    }
}

enum UnityBridgeMessengerError : Error {
    case notInitialized
}

class UnityBridgeMessenger : BridgeMessenger {
    
    let gameObject : String
    let method : String
    
    let encoder = JSONEncoder()
    
    init(gameObject: String, method: String) {
        self.gameObject = gameObject
        self.method = method
    }
    
    func sendMessage(path: String, content: String) throws {
        let payload = BridgeMessage(path: path, content: content)
        let message = String(decoding: try encoder.encode(payload), as: UTF8.self)
        Task { @MainActor in
            guard let appDelegate = UIApplication.shared.delegate as? AppDelegate else {
                throw UnityBridgeMessengerError.notInitialized
            }
            appDelegate.sendMessageToGO(withName: gameObject, functionName: method, message: message)
        }
    }
}

struct TestPayload : Codable {
    var name : String
    var number : Int
    var duration : Double
}

struct TestResult : Codable {
    var message : String
    var processed : Int
}


