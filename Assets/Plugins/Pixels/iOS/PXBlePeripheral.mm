#import "PXBlePeripheral.h"


@implementation PXBlePeripheral

// https://github.com/NordicSemiconductor/Android-BLE-Library/blob/master/ble/src/main/java/no/nordicsemi/android/ble/callback/FailCallback.java
//int REASON_DEVICE_DISCONNECTED = -1;
//int REASON_DEVICE_NOT_SUPPORTED = -2;
//int REASON_NULL_ATTRIBUTE = -3;
//int REASON_REQUEST_FAILED = -4;
//int REASON_TIMEOUT = -5;
//int REASON_VALIDATION = -6;
//int REASON_CANCELLED = -7;
//int REASON_BLUETOOTH_DISABLED = -100;

static NSError *notConnectedError = [NSError errorWithDomain:[NSString stringWithFormat:@"%@.errorDomain", [[NSBundle mainBundle] bundleIdentifier]]
                                                        code:-1 // Same value as Nordic's Android BLE library
                                                    userInfo:@{ NSLocalizedDescriptionKey: @"Not connected" }];

// static NSError *deviceNotSupportedError = [NSError errorWithDomain:[NSString stringWithFormat:@"%@.errorDomain", [[NSBundle mainBundle] bundleIdentifier]]
//                                                               code:-2 // Same value as Nordic's Android BLE library
//                                                           userInfo:@{ NSLocalizedDescriptionKey: @"Device not supported" }];

static NSError *nullAttributeError = [NSError errorWithDomain:[NSString stringWithFormat:@"%@.errorDomain", [[NSBundle mainBundle] bundleIdentifier]]
                                                         code:-3 // Same value as Nordic's Android BLE library
                                                     userInfo:@{ NSLocalizedDescriptionKey: @"Null attribute" }];

// static NSError *discoveryError = [NSError errorWithDomain:[NSString stringWithFormat:@"%@.errorDomain", [[NSBundle mainBundle] bundleIdentifier]]
//                                                      code:-8
//                                                  userInfo:@{ NSLocalizedDescriptionKey: @"Discover error" }];

static NSError *bluetoothDisabledError = [NSError errorWithDomain:[NSString stringWithFormat:@"%@.errorDomain", [[NSBundle mainBundle] bundleIdentifier]]
                                                             code:-100 // Same value as Nordic's Android BLE library
                                                         userInfo:@{ NSLocalizedDescriptionKey: @"Bluetooth disabled" }];

static NSError *connectionError = [NSError errorWithDomain:[NSString stringWithFormat:@"%@.errorDomain", [[NSBundle mainBundle] bundleIdentifier]]
                                                             code:-99
                                                         userInfo:@{ NSLocalizedDescriptionKey: @"Connection error" }];

//
// Getters
//

- (NSUUID *)identifier
{
    return _peripheral.identifier;
}

- (bool)isConnected
{
    return _peripheral.state == CBPeripheralStateConnected;
}

- (int)rssi
{
    return _rssi;
}

//
// Public methods
//

- (instancetype)initWithPeripheral:(CBPeripheral *)peripheral
            centralManagerDelegate:(PXBleCentralManagerDelegate *)centralManagerDelegate
    connectionStatusChangedHandler:(void (^)(PXBlePeripheralConnectionEvent connectionEvent, PXBlePeripheralConnectionEventReason reason))connectionEventHandler;
{
    if (self = [super init])
    {
        if (!peripheral || !centralManagerDelegate)
        {
            return nil;
        }
        
        _queue = GetBleSerialQueue();
        _centralDelegate = centralManagerDelegate;
        _peripheral = peripheral;
        _peripheral.delegate = self;
        _connectInProgress = _disconnectInProgress = false;
        _connectionEventHandler = connectionEventHandler;
        _rssi = 0;
        _pendingRequests = [NSMutableArray<NSError *(^)()> new];
        _completionHandlers = [NSMutableArray<void (^)(NSError *error)> new];
        _valueChangedHandlers = [NSMapTable<CBCharacteristic *, void (^)(CBCharacteristic *characteristic, NSError *error)> strongToStrongObjectsMapTable];
        
        __weak PXBlePeripheral *weakSelf = self;
        PXBlePeripheralConnectionEventHandler handler =
        ^(CBPeripheral *peripheral, PXBlePeripheralConnectionEvent connectionEvent, NSError *error)
        {
            // Be sure to not use self directly (or implictly by referencing a property)
            // otherwise it creates a strong reference to itself and prevents the instance's deallocation
            PXBlePeripheral *strongSelf = weakSelf;
            if (strongSelf)
            {
                switch (connectionEvent)
                {
                    case PXBlePeripheralConnectionEventConnected:
                        NSLog(@">> PeripheralConnectionEvent = connected");
                        // We must discover services and characteristics before we can use them
                        [peripheral discoverServices:strongSelf->_requiredServices];
                        break;
                        
                    case PXBlePeripheralConnectionEventDisconnected:
                    {
                        NSLog(@">> PeripheralConnectionEvent = disconnected with error %@", error);
                        PXBlePeripheralConnectionEventReason reason = strongSelf->_discoveryDisconnectReason;
                        if (reason != PXBlePeripheralConnectionEventReasonSuccess)
                        {
                            strongSelf->_discoveryDisconnectReason = PXBlePeripheralConnectionEventReasonSuccess;
                        }
                        else if (!strongSelf->_disconnectInProgress)
                        {
                            reason = PXBlePeripheralConnectionEventReasonTimeout;
                        }
                        
                        strongSelf->_disconnectInProgress = false;
                        if (strongSelf->_connectInProgress)
                        {
                            error = connectionError;
                        }
                        
                        if (strongSelf->_connectionEventHandler)
                        {
                            strongSelf->_connectionEventHandler(PXBlePeripheralConnectionEventDisconnected, reason);
                        }
                        // We're now disconnected, notify error and clear any pending request
                        // Note: we could skip clear when there is no error (meaning that the disconnect was intentional)
                        //       but we're doing the same as in Nordic's Android BLE library
                        //TODO really?
                        [strongSelf reportRequestResult:error clearPendingRequests:true];
                        break;
                    }
                        
                    case PXBlePeripheralConnectionEventFailedToConnect:
                        NSLog(@">> PeripheralConnectionEvent = failed with error %@", error);
                        if (strongSelf->_connectionEventHandler)
                        {
                            strongSelf->_connectionEventHandler(PXBlePeripheralConnectionEventDisconnected, PXBlePeripheralConnectionEventReasonTimeout);
                        }

                        [strongSelf reportRequestResult:error];
                        break;
                        
                    default:
                        NSLog(@">> PeripheralConnectionEvent = ???"); //TODO
                        break;
                }
            }
        };
        [_centralDelegate setConnectionEventHandler:handler
                                      forPeripheral:_peripheral];
    }
    return self;
}

- (void)dealloc
{
    // No need to call super dealloc a ARC is enabled
    [_centralDelegate.centralManager cancelPeripheralConnection:_peripheral];
    NSLog(@"PXBlePeripheral dealloc");
}


- (void)cancelQueue
{
    NSLog(@">> cancelQueue");
    
    @synchronized (_pendingRequests)
    {
        void (^handler)(NSError *error) = nil;
        bool keepFirst = _completionHandlers.count > _pendingRequests.count;
        if (keepFirst)
        {
            handler = _completionHandlers[0];
        }
        
        [_pendingRequests removeAllObjects];
        [_completionHandlers removeAllObjects];
        if (keepFirst)
        {
            [_completionHandlers addObject:handler];
        }
    }
    
    if (_connectInProgress)
    {
        _discoveryDisconnectReason = PXBlePeripheralConnectionEventReasonCanceled;
        [_centralDelegate.centralManager cancelPeripheralConnection:self->_peripheral];
    }
}

- (void)queueConnectWithServices:(NSArray<CBUUID *> *)services
               completionHandler:(void (^)(NSError *error))completionHandler
{
    NSLog(@">> queueConnect");
    
    NSArray<CBUUID *> *requiredServices = [services copy];
    [self queueRequest:^{
        NSLog(@">> Connect");
        _connectInProgress = true;
        if (self->_connectionEventHandler)
        {
            self->_connectionEventHandler(PXBlePeripheralConnectionEventConnecting, PXBlePeripheralConnectionEventReasonSuccess);
        }
        _requiredServices = requiredServices;
        [_centralDelegate.centralManager connectPeripheral:self->_peripheral options:nil];
        return (NSError *)nil;
    }
     completionHandler:^(NSError *error) {
        NSLog(@">> Connect result %@", error);
        _connectInProgress = false;
        if (completionHandler)
        {
            completionHandler(error);
        }
    }];
}

- (void)queueDisconnect:(void (^)(NSError *error))completionHandler
{
    NSLog(@">> queueDisconnect");
    
    [self queueRequest:^{
        NSLog(@">> Disconnect");
        self->_disconnectInProgress = true;
        if (self->_connectionEventHandler)
        {
            self->_connectionEventHandler(PXBlePeripheralConnectionEventDisconnecting, PXBlePeripheralConnectionEventReasonSuccess);
        }
        [self->_centralDelegate.centralManager cancelPeripheralConnection:self->_peripheral];
        return (NSError *)nil;
    }
     completionHandler:completionHandler];
}

- (void)queueReadRssi:(void (^)(NSError *error))completionHandler
{
    NSLog(@">> queueReadRsssi");
    
    [self queueRequest:^{
        NSLog(@">> ReadRSSI");
        [self->_peripheral readRSSI];
        return (NSError *)nil;
    }
     completionHandler:completionHandler];
}

- (void)queueReadValueForCharacteristic:(CBCharacteristic *)characteristic
                    valueChangedHandler:(void (^)(CBCharacteristic *characteristic, NSError *error))valueChangedHandler
                      completionHandler:(void (^)(NSError *error))completionHandler
{
    NSLog(@">> queueReadValueForCharacteristic");
    
    [self queueRequest:^{
        if (!characteristic || !valueChangedHandler || !self.isConnected)
        {
            NSLog(@">> ReadValueForCharacteristic -> invalid call");
            return [NSError errorWithDomain:CBErrorDomain code:CBErrorUnknown userInfo:0]; //TODO
        }
        
        NSLog(@">> ReadValueForCharacteristic");
        [self->_valueChangedHandlers setObject:valueChangedHandler forKey:characteristic];
        [self->_peripheral readValueForCharacteristic:characteristic];
        return (NSError *)nil;
    }
     completionHandler:completionHandler];
}

- (void)queueWriteValue:(NSData *)data
      forCharacteristic:(CBCharacteristic *)characteristic
                   type:(CBCharacteristicWriteType)type
      completionHandler:(void (^)(NSError *error))completionHandler
{
    NSLog(@">> queueWriteValue");
    
    [self queueRequest:^{
        if (!characteristic || !self.isConnected)
        {
            NSLog(@">> WriteValue -> invalid call");
            return [NSError errorWithDomain:CBErrorDomain code:CBErrorUnknown userInfo:0]; //TODO
        }
        
        NSLog(@">> WriteValue");
        [self->_peripheral writeValue:data forCharacteristic:characteristic type:type];
        if (type == CBCharacteristicWriteWithoutResponse)
        {
            [self reportRequestResult:nil];
        }
        return (NSError *)nil;
    }
     completionHandler:completionHandler];
}

- (void)queueSetNotifyValueForCharacteristic:(CBCharacteristic *)characteristic
                         valueChangedHandler:(void (^)(CBCharacteristic *characteristic, NSError *error))valueChangedHandler
                           completionHandler:(void (^)(NSError *error))completionHandler
{
    NSLog(@">> queueSetNotifyValueForCharacteristic");
    
    [self queueRequest:^{
        if (!characteristic || !valueChangedHandler || !self.isConnected)
        {
            NSLog(@">> SetNotifyValueForCharacteristic -> invalid call");
            return [NSError errorWithDomain:CBErrorDomain code:CBErrorUnknown userInfo:0]; //TODO
        }
        
        NSLog(@">> SetNotifyValueForCharacteristic");
        [self->_valueChangedHandlers setObject:valueChangedHandler forKey:characteristic];
        [self->_peripheral setNotifyValue:valueChangedHandler != nil forCharacteristic:characteristic];
        return (NSError *)nil;
    }
     completionHandler:completionHandler];
}

//
// Private methods
//

// completionHandler can be nil
- (void)queueRequest:(NSError *(^)())requestBlock completionHandler:(void (^)(NSError *error))completionHandler
{
    NSAssert(requestBlock, @"Nil operation block");
    
    dispatch_async(_queue, ^{
        bool runNow = false;
        @synchronized (self->_pendingRequests)
        {
            // Queue request and completion handler
            [self->_pendingRequests addObject:requestBlock];
            [self->_completionHandlers addObject:completionHandler];
            
            // Process request immediately if queue was empty
            runNow =  self->_completionHandlers.count == 1;
        }
        
        if (runNow)
        {
            [self runNextRequest];
        }
    });
}

- (void)runNextRequest
{
    NSError *(^requestBlock)() = nil;
    @synchronized (_pendingRequests)
    {
        if (_pendingRequests.count > 0)
        {
            NSLog(@">> runNextRequest");
            
            requestBlock = _pendingRequests[0];
            [_pendingRequests removeObjectAtIndex:0];
            
            assert(requestBlock);
        }
    }
    
    if (requestBlock)
    {
        NSError * error = requestBlock();
        if (error)
        {
            [self reportRequestResult:error];
        }
    }
}

// Should always be called on the queue
- (void)reportRequestResult:(NSError *)error
{
    [self reportRequestResult:error clearPendingRequests:false];
}

// Should always be called on the queue
- (void)reportRequestResult:(NSError *)error clearPendingRequests:(bool)clearPendingRequests
{
    void (^handler)(NSError *error) = nil;
    
    @synchronized (_pendingRequests)
    {
        NSAssert(clearPendingRequests || (_completionHandlers.count > 0), @"Empty _completionHandlers");
        
        if (_completionHandlers.count > 0)
        {
            NSLog(@">> reportRequestResult %@", [error localizedDescription]);
            
            handler = _completionHandlers[0];
            if (clearPendingRequests)
            {
                [_pendingRequests removeAllObjects];
                [_completionHandlers removeAllObjects];
            }
            else
            {
                [_completionHandlers removeObjectAtIndex:0];
            }
        }
        else if (clearPendingRequests)
        {
            if (_pendingRequests.count > 0)
            {
                NSLog(@">> _pendingRequests not empty!!");
                [_pendingRequests removeAllObjects]; // Should be empty anyways
            }
        }
        else
        {
            NSLog(@">> _completionHandlers empty!!");
        }
    }
    
    if (handler)
    {
        handler(error);
    }
    
    [self runNextRequest];
}

- (void)disconnectForDiscoveryError:(PXBlePeripheralConnectionEventReason)reason
{
    NSAssert(!_discoveryDisconnectReason, @"_discoveryDisconnectReason already set");
    
    _discoveryDisconnectReason = reason;
    [_centralDelegate.centralManager cancelPeripheralConnection:_peripheral];
}

- (bool)hasAllRequiredServices:(NSArray<CBService *> *)services
{
    for (CBUUID *uuid in _requiredServices)
    {
        bool found = false;
        for (CBService *service in services)
        {
            found = [service.UUID isEqual:uuid];
            if (found) break;
        }
        if (!found) return false;
    }
    return true;
}

//
// CBPeripheralDelegate implementation
//

- (void)peripheral:(CBPeripheral *)peripheral didDiscoverServices:(NSError *)error
{
    if (error)
    {
        [self disconnectForDiscoveryError:PXBlePeripheralConnectionEventReasonUnknown];
    }
    else if (![self hasAllRequiredServices:peripheral.services])
    {
        [self disconnectForDiscoveryError:PXBlePeripheralConnectionEventReasonNotSupported];
    }
    else
    {
        // Store number of services to discover, we'll consider to be fully connected
        // only all the services have been discovered
        _discoveringServicesCounter = peripheral.services.count;
        
        for (CBService *service in peripheral.services)
        {
            [peripheral discoverCharacteristics:nil forService:service];
        }
    }
}

- (void)peripheral:(CBPeripheral *)peripheral didDiscoverCharacteristicsForService:(CBService *)service error:(NSError *)error
{
    if (error)
    {
        [self disconnectForDiscoveryError:PXBlePeripheralConnectionEventReasonUnknown];
    }
    else
    {
        assert(_discoveringServicesCounter > 0);
        --_discoveringServicesCounter;
        if (_discoveringServicesCounter == 0)
        {
            // Notify connected when characteristics are discovered for all services
            // We must assume that each service will at least report one characteristic
            [self reportRequestResult:error];
            if (_connectionEventHandler)
            {
                _connectionEventHandler(PXBlePeripheralConnectionEventReady, PXBlePeripheralConnectionEventReasonSuccess);
            }
        }
    }
}

// - (void)peripheral:(CBPeripheral *)peripheral didDiscoverDescriptorsForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error
// {
// }

- (void)peripheral:(CBPeripheral *)peripheral didUpdateValueForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error
{
    NSLog(@">> didUpdateValueForCharacteristic with error %@", error);
    void (^handler)(CBCharacteristic *characteristic, NSError *error) = [_valueChangedHandlers objectForKey:characteristic];
    if (handler)
    {
        handler(characteristic, error);
    }
}

// - (void)peripheral:(CBPeripheral *)peripheral didUpdateValueForDescriptor:(CBDescriptor *)descriptor error:(NSError *)error
// {
// }

- (void)peripheral:(CBPeripheral *)peripheral didWriteValueForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error
{
    NSLog(@">> didWriteValueForCharacteristic with error %@", error);
    [self reportRequestResult:error];
}

// - (void)peripheral:(CBPeripheral *)peripheral didWriteValueForDescriptor:(CBDescriptor *)descriptor error:(NSError *)error
// {
// }

// - (void)peripheralIsReadyToSendWriteWithoutResponse:(CBPeripheral *)peripheral
// {
// }

- (void)peripheral:(CBPeripheral *)peripheral didUpdateNotificationStateForCharacteristic:(CBCharacteristic *)characteristic error:(NSError *)error
{
    NSLog(@">> didUpdateNotificationStateForCharacteristic with error %@", error);
    [self reportRequestResult:error];
}

- (void)peripheral:(CBPeripheral *)peripheral didReadRSSI:(NSNumber *)RSSI error:(NSError *)error
{
    NSLog(@">> didReadRSSI with error %@", error);
    _rssi = RSSI.intValue;
    [self reportRequestResult:error];
}

// - (void)peripheralDidUpdateName:(CBPeripheral *)peripheral
// {
// }

@end
