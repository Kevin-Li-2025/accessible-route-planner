import React, { useState, useEffect } from "react";
import {
  View,
  Text,
  TextInput,
  TouchableOpacity,
  StyleSheet,
  Image,
  ScrollView,
  SafeAreaView,
  KeyboardAvoidingView,
  Platform,
  Dimensions,
  StatusBar,
  Alert,
  ActivityIndicator,
} from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { AntDesign, FontAwesome, Ionicons } from "@expo/vector-icons";
import Animated, {
  useSharedValue,
  useAnimatedStyle,
  withSpring,
  withTiming,
  interpolate,
} from "react-native-reanimated";
import { router } from "expo-router";
import { useAuth } from "@/context/AuthContext";
import { ErrorMessage } from "@/components/ErrorMessage";
import { useFormAnimation } from "@/hooks/use-form-animation";

const { width } = Dimensions.get("window");

export default function AuthScreen() {
  const [isSignup, setIsSignup] = useState(false);
  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  
  const { signIn, signUp, isAuthenticated } = useAuth();
  const { shake, animatedStyle: shakeStyle } = useFormAnimation();
  const animation = useSharedValue(0);

  useEffect(() => {
    if (isAuthenticated) {
      router.replace("/(tabs)/map");
    }
  }, [isAuthenticated]);

  useEffect(() => {
    animation.value = withSpring(isSignup ? 1 : 0, {
      damping: 20,
      stiffness: 90,
    });
    setError(null); // Clear errors on tab switch
  }, [isSignup, animation]);

  const handleSubmit = async () => {
    setError(null);
    if (!email || !password || (isSignup && !fullName)) {
      setError("Please fill in all mandatory fields");
      shake();
      return;
    }

    setIsSubmitting(true);
    try {
      if (isSignup) {
        await signUp(email, password, fullName);
      } else {
        await signIn(email, password);
      }
      router.replace("/(tabs)/map");
    } catch (err: any) {
      setError(err.message || "Failed to authenticate. Please try again.");
      shake();
    } finally {
      setIsSubmitting(false);
    }
  };

  const animatedSwitchStyle = useAnimatedStyle(() => {
    const translateX = interpolate(animation.value, [0, 1], [4, (width - 40 - 48 - 8) / 2 + 4]);
    return {
      transform: [{ translateX }],
    };
  });

  const animatedContentStyle = useAnimatedStyle(() => {
    return {
      opacity: withTiming(1, { duration: 300 }),
    };
  });

  return (
    <SafeAreaView style={styles.safeArea}>
      <StatusBar barStyle="dark-content" />
      <KeyboardAvoidingView
        behavior={Platform.OS === "ios" ? "padding" : "height"}
        style={{ flex: 1 }}
      >
        <ScrollView
          contentContainerStyle={styles.scrollContent}
          showsVerticalScrollIndicator={false}
          bounces={false}
        >
          <View style={styles.container}>
            <View style={styles.topHeader}>
              <View style={styles.topShape} />
              <Image
                source={require("../assets/images/logo.png")}
                style={styles.logo}
                resizeMode="contain"
              />
              <Text style={styles.title}>
                Access<Text style={styles.cityText}>City</Text>
              </Text>
              <Text style={styles.subtitle}>
                Navigate your world with confidence
              </Text>
            </View>

            <Animated.View style={[styles.authCard, shakeStyle]}>
              <View style={styles.switchContainer}>
                <Animated.View style={[styles.switchSlider, animatedSwitchStyle]} />
                <TouchableOpacity
                  style={styles.tabButton}
                  onPress={() => setIsSignup(false)}
                  activeOpacity={1}
                >
                  <Text style={[styles.tabText, !isSignup && styles.activeTabText]}>
                    Log In
                  </Text>
                </TouchableOpacity>

                <TouchableOpacity
                  style={styles.tabButton}
                  onPress={() => setIsSignup(true)}
                  activeOpacity={1}
                >
                  <Text style={[styles.tabText, isSignup && styles.activeTabText]}>
                    Sign Up
                  </Text>
                </TouchableOpacity>
              </View>

              <View style={styles.contentArea}>
                <View style={styles.socialRow}>
                  <TouchableOpacity style={styles.socialButton}>
                    <AntDesign name="google" size={24} color="#EA4335" />
                  </TouchableOpacity>
                  <TouchableOpacity style={styles.socialButton}>
                    <FontAwesome name="facebook" size={24} color="#1877F2" />
                  </TouchableOpacity>
                  <TouchableOpacity style={styles.socialButton}>
                    <AntDesign name="apple" size={24} color="#000" />
                  </TouchableOpacity>
                </View>

                <View style={styles.dividerRow}>
                  <View style={styles.dividerLine} />
                  <Text style={styles.dividerText}>or use your email</Text>
                  <View style={styles.dividerLine} />
                </View>

                <Animated.View style={[styles.form, animatedContentStyle]}>
                  {isSignup && (
                    <View>
                      <View style={styles.inputContainer}>
                        <Ionicons name="person-outline" size={20} color="#94A3B8" style={styles.inputIcon} />
                        <TextInput
                          placeholder="Full Name"
                          placeholderTextColor="#94A3B8"
                          style={styles.input}
                          value={fullName}
                          onChangeText={setFullName}
                        />
                      </View>
                    </View>
                  )}
                  <View>
                    <View style={styles.inputContainer}>
                      <Ionicons name="mail-outline" size={20} color="#94A3B8" style={styles.inputIcon} />
                      <TextInput
                        placeholder="Email Address"
                        placeholderTextColor="#94A3B8"
                        keyboardType="email-address"
                        autoCapitalize="none"
                        style={styles.input}
                        value={email}
                        onChangeText={setEmail}
                      />
                    </View>
                  </View>
                  <View>
                    <View style={styles.inputContainer}>
                      <Ionicons name="lock-closed-outline" size={20} color="#94A3B8" style={styles.inputIcon} />
                      <TextInput
                        placeholder="Password"
                        placeholderTextColor="#94A3B8"
                        secureTextEntry
                        style={styles.input}
                        value={password}
                        onChangeText={setPassword}
                      />
                    </View>
                    <ErrorMessage visible={!!error} message={error ?? undefined} />
                  </View>
                  
                  {!isSignup && (
                    <TouchableOpacity 
                      style={styles.forgotPasswordContainer}
                      onPress={() => router.push("/forgot-password")}
                    >
                      <Text style={styles.forgotPasswordText}>Forgot Password?</Text>
                    </TouchableOpacity>
                  )}

                  <TouchableOpacity 
                    activeOpacity={0.85} 
                    style={styles.mainButtonContainer}
                    onPress={handleSubmit}
                    disabled={isSubmitting}
                  >
                    <LinearGradient
                      colors={["#2563EB", "#1D4ED8"]}
                      style={styles.mainButton}
                    >
                      {isSubmitting ? (
                        <ActivityIndicator color="#FFF" />
                      ) : (
                        <Text style={styles.mainButtonText}>
                          {isSignup ? "Create Account" : "Log In"}
                        </Text>
                      )}
                    </LinearGradient>
                  </TouchableOpacity>
                </Animated.View>
              </View>
            </Animated.View>
          </View>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}


const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#F8FAFC",
  },
  scrollContent: {
    flexGrow: 1,
  },
  container: {
    flex: 1,
    paddingHorizontal: 20,
  },
  topHeader: {
    alignItems: "center",
    paddingTop: 60,
    paddingBottom: 40,
  },
  topShape: {
    position: "absolute",
    top: -120,
    right: -60,
    width: 280,
    height: 280,
    borderRadius: 140,
    backgroundColor: "#DBEAFE",
    opacity: 0.5,
  },
  logo: {
    width: 90,
    height: 90,
    marginBottom: 20,
  },
  title: {
    fontSize: 34,
    fontWeight: "900",
    color: "#0F172A",
    letterSpacing: -1,
  },
  cityText: {
    color: "#2563EB",
  },
  subtitle: {
    fontSize: 16,
    color: "#64748B",
    marginTop: 6,
    fontWeight: "500",
  },
  authCard: {
    backgroundColor: "#FFFFFF",
    borderRadius: 36,
    padding: 24,
    shadowColor: "#0F172A",
    shadowOffset: { width: 0, height: 20 },
    shadowOpacity: 0.1,
    shadowRadius: 30,
    elevation: 10,
    marginBottom: 40,
  },
  switchContainer: {
    flexDirection: "row",
    backgroundColor: "#F1F5F9",
    borderRadius: 18,
    padding: 4,
    position: "relative",
    marginBottom: 40,
  },
  switchSlider: {
    position: "absolute",
    top: 4,
    bottom: 4,
    width: (width - 40 - 48 - 8) / 2,
    backgroundColor: "#FFFFFF",
    borderRadius: 14,
    shadowColor: "#000",
    shadowOffset: { width: 0, height: 4 },
    shadowOpacity: 0.1,
    shadowRadius: 8,
    elevation: 3,
  },
  tabButton: {
    flex: 1,
    paddingVertical: 14,
    alignItems: "center",
    zIndex: 1,
  },
  tabText: {
    fontSize: 15,
    fontWeight: "700",
    color: "#94A3B8",
  },
  activeTabText: {
    color: "#0F172A",
  },
  contentArea: {
    flex: 1,
  },
  socialRow: {
    flexDirection: "row",
    justifyContent: "center",
    gap: 20,
    marginBottom: 32,
  },
  socialButton: {
    width: 64,
    height: 64,
    borderRadius: 22,
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#E2E8F0",
    alignItems: "center",
    justifyContent: "center",
    shadowColor: "#000",
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.05,
    shadowRadius: 4,
  },
  dividerRow: {
    flexDirection: "row",
    alignItems: "center",
    marginBottom: 32,
  },
  dividerLine: {
    flex: 1,
    height: 1,
    backgroundColor: "#F1F5F9",
  },
  dividerText: {
    marginHorizontal: 16,
    fontSize: 14,
    color: "#94A3B8",
    fontWeight: "600",
  },
  form: {
    gap: 20,
  },
  inputContainer: {
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: "#F8FAFC",
    borderRadius: 18,
    borderWidth: 1,
    borderColor: "#E2E8F0",
    paddingHorizontal: 18,
  },
  inputIcon: {
    marginRight: 14,
  },
  input: {
    flex: 1,
    height: 60,
    fontSize: 16,
    color: "#0F172A",
    fontWeight: "600",
  },
  forgotPasswordContainer: {
    alignSelf: "flex-end",
    marginTop: -8,
  },
  forgotPasswordText: {
    color: "#2563EB",
    fontSize: 14,
    fontWeight: "700",
  },
  mainButtonContainer: {
    marginTop: 12,
    borderRadius: 20,
    shadowColor: "#2563EB",
    shadowOffset: { width: 0, height: 10 },
    shadowOpacity: 0.3,
    shadowRadius: 20,
    elevation: 6,
  },
  mainButton: {
    height: 64,
    borderRadius: 20,
    alignItems: "center",
    justifyContent: "center",
  },
  mainButtonText: {
    color: "#FFFFFF",
    fontSize: 18,
    fontWeight: "800",
    letterSpacing: 0.5,
  },
});